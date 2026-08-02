using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Norse.Abstractions.Contracts;
using Norse.Abstractions.Web.Server.Mediator;
using Norse.Infrastructure.Web.Client.Grpc;
using Norse.Infrastructure.Web.Server.Mediator;
using Norse.Infrastructure.Web.Server.Mediator.Grpc;
using Norse.Persistence.EntityFramework;
using Norse.Persistence.EntityFramework.PostgreSQL;
using Norse.Primitives.Identifiers;
using Norse.Reference;
using Norse.Reference.Data.EntityFramework;
using Norse.Reference.Data.EntityFramework.Migrations;
using Norse.Reference.Web.Server;
using ProtoBuf.Grpc.Client;
using Testcontainers.PostgreSql;

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
/// A real Postgres container, migrated and seeded through the exact same contributors the migrations
/// service runs (<see cref="NorseReferenceMigrationContributor"/>/<see cref="ReferenceDataSeedContributor"/>),
/// standing behind a real <see cref="TestServer"/>/HTTP-2 gRPC round trip -- the "Heimdall spike"
/// pattern the plan names, transplanted onto the well-and-wire read path: real DB, real wire, no
/// hand-wired stub handler anywhere in this fixture.
/// </summary>
public sealed class CountryLookupPostgresFixture : IAsyncLifetime
{
	readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:19beta2")
		.WithDatabase("norse_reference")
		.Build();

	// null! justified: hydrated by InitializeAsync before xUnit hands the fixture to any test.
	public string ConnectionString { get; private set; } = null!;

	public async ValueTask InitializeAsync()
	{
		await _container.StartAsync();
		ConnectionString = _container.GetConnectionString();

		DbContextOptionsBuilder<ReferenceDbContext> optionsBuilder = new();
		optionsBuilder.ApplyNorseProviderOptions(NorsePostgresEfProvider.Instance,
			ConnectionString, typeof(NorseReferenceMigrationContributor).Assembly.GetName().Name);
		await using ReferenceDbContext context = new(optionsBuilder.Options);

		await new NorseReferenceMigrationContributor(context).MigrateAsync(CancellationToken.None);
		await new ReferenceDataSeedContributor(context).SeedAsync(CancellationToken.None);
	}

	public ValueTask DisposeAsync() =>
		_container.DisposeAsync();
}

[CollectionDefinition("CountryLookupPostgres")]
[SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "xUnit collection fixture naming convention")]
public sealed class CountryLookupPostgresCollection : ICollectionFixture<CountryLookupPostgresFixture>;

[Collection("CountryLookupPostgres")]
public sealed class CountryLookupE2ETests(CountryLookupPostgresFixture fixture)
{
	static async Task<IHost> CreateHostAsync(string connectionString, CancellationToken cancellationToken)
	{
		// TestServer's in-memory handler has no TLS to negotiate -- real deployments always terminate
		// TLS in front of the gRPC endpoint, but Grpc.Net.Client refuses even to attempt an HTTP/2 call
		// over a plain "http://" address without this opt-in (mirrors MediatorParityTests/WirePathAuthorizationTests).
		AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

		// Real deployments reach this through MapNorseGrpcServices(), which calls it before mapping --
		// this host maps ReferenceService directly (mirrors MediatorParityTests), so the payload
		// surrogate (idempotent, generated) needs registering explicitly here instead.
		NorseGrpcServerRegistration.RegisterNorseOutcomeSurrogates();

		var principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test"));

		return await new HostBuilder()
			.ConfigureWebHost(webHost =>
			{
				webHost.UseTestServer();
				webHost.ConfigureServices(services =>
				{
					services.AddLogging();
					services.AddAuthorizationBuilder().AddPolicy(ReferencePolicies.Public, p => p.RequireAssertion(_ => true));
					services.AddNorsePipeline();
					services.AddNorseCodeFirstGrpc();
					services.AddRouting();

					// The real composition-root extension (Task 13) -- AddDbContextFactory<ReferenceDbContext>,
					// AddWell<ReferenceDbContext>(), the generated handler/dispatch registration, and
					// IReferenceService itself -- against the migrated, seeded container above. No stub
					// handler anywhere: this is the actual read path, over a real database.
					services.AddNorseReferenceService(connectionString);

					// Mirrors MediatorParityTests: AuthorizationBehavior needs a principal to ask about,
					// and this suite has neither a circuit nor a cookie scheme to seed one for real.
					// ReferencePolicies.Public's permissive assertion means any principal satisfies it.
					services.AddScoped<IPrincipalAccessor>(_ => new ReferenceTestPrincipalAccessor(principal));
				});
				webHost.Configure(app =>
				{
					app.UseRouting();
					app.UseEndpoints(endpoints => endpoints.MapGrpcService<ReferenceService>());
				});
			})
			.StartAsync(cancellationToken);
	}

	static IReferenceService CreateWireClient(IHost host)
	{
		var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
		var invoker = channel.Intercept(new OutcomeClientInterceptor());
		return GrpcClientFactory.CreateGrpcService<IReferenceService>(invoker);
	}

	[Theory]
	[InlineData("US", "840")]
	[InlineData("USA", "840")]
	[InlineData("840", "840")]
	[InlineData("40", "040")]
	async Task The_wire_uuid_round_trips_byte_identical_to_the_client_side_v5(string code, string expectedD3)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var host = await CreateHostAsync(fixture.ConnectionString, cancellationToken);

		var outcome = await CreateWireClient(host).GetCountry(new() { Code = code }, cancellationToken);

		var response = outcome.Match(static r => r, static p => throw new InvalidOperationException(p.Category.ToString()));

		// Recompute client-side from the frozen name form -- Guid equality AND canonical string
		// equality (byte-order settled law: this assertion is its only mention, per spec §1).
		DeterministicGuid local = new(ReferenceNamespaces.Iso3166, expectedD3);
		response.Id.ShouldBe(local.Value);
		response.Id.ToString().ShouldBe(local.Value.ToString());
	}

	[Fact]
	async Task Garbage_maps_to_invalid_argument_on_the_wire()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var host = await CreateHostAsync(fixture.ConnectionString, cancellationToken);

		var exception = await Should.ThrowAsync<RpcException>(async () =>
			await CreateWireClient(host).GetCountry(new() { Code = "banana" }, cancellationToken));

		exception.StatusCode.ShouldBe(StatusCode.InvalidArgument);
		var problem = exception.DecodeProblem();
		problem.Category.ShouldBe(ErrorCategory.Validation);
		problem.Errors["code"].ShouldContain("banana");
	}
}

/// <summary>
/// Overrides <c>AddNorsePipeline()</c>'s own <see cref="IPrincipalAccessor"/> registration (DI resolves
/// the last registration for a single-service ask) so <c>AuthorizationBehavior</c> always has a
/// principal to ask about -- mirrors <c>MediatorParityTests.ReferenceTestPrincipalAccessor</c> exactly.
/// </summary>
sealed class ReferenceTestPrincipalAccessor(ClaimsPrincipal principal) : IPrincipalAccessor
{
	public ValueTask<ClaimsPrincipal> GetPrincipalAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(principal);
}
