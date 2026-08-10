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
using Norse.Infrastructure.Persistence.EntityFramework;
using Norse.Infrastructure.Web.Client.Grpc;
using Norse.Infrastructure.Web.Server.Mediator;
using Norse.Infrastructure.Web.Server.Mediator.Grpc;
using Norse.Persistence.EntityFramework;
using Norse.Persistence.EntityFramework.PostgreSQL;
using Norse.Primitives.Identifiers;
using Norse.Reference;
using Norse.Reference.Data.EntityFramework;
using Norse.Reference.Data.EntityFramework.Migrations;
using Norse.Reference.Data.EntityFramework.Migrations.PostgreSQL;
using Norse.Reference.Web.Server;
using ProtoBuf.Grpc.Client;
using ProtoBuf.Meta;
using Testcontainers.PostgreSql;

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
///     A real Postgres container, migrated and seeded through the exact same contributors the migrations
///     service runs (<see cref="NorseReferenceMigrationContributor" />/<see cref="ReferenceDataSeedContributor" />),
///     standing behind a real <see cref="TestServer" />/HTTP-2 gRPC round trip -- the "Heimdall spike"
///     pattern the plan names, transplanted onto the well-and-wire read path: real DB, real wire, no
///     hand-wired stub handler anywhere in this fixture.
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
			ConnectionString, typeof(ReferenceDbContextFactory).Assembly.GetName().Name);
		await using ReferenceDbContext context = new(optionsBuilder.Options);

		await new NorseReferenceMigrationContributor(context).MigrateAsync(CancellationToken.None);
		await new ReferenceDataSeedContributor(context).SeedAsync(CancellationToken.None);
	}

	public ValueTask DisposeAsync() =>
		_container.DisposeAsync();
}

[CollectionDefinition("CountryLookupPostgres")]
[SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix",
	Justification = "xUnit collection fixture naming convention")]
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
		// this host maps ReferenceService directly (mirrors MediatorParityTests). No need to register the
		// payload surrogate here: WireModelFixture (an [assembly: AssemblyFixture]) already did it,
		// guaranteed complete before any test in this assembly runs.

		var principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test"));

		return await new HostBuilder()
			.ConfigureWebHost(webHost =>
			{
				webHost.UseTestServer();
				webHost.ConfigureServices(services =>
				{
					services.AddLogging();
					services.AddAuthorizationBuilder()
						.AddPolicy(ReferencePolicies.Public, p => p.RequireAssertion(_ => true));
					services.AddNorsePipeline();
					services.AddNorseCodeFirstGrpc();
					services.AddRouting();

					// The real realm extension -- AddDbContextFactory<ReferenceDbContext>, the generated
					// handler/dispatch registration, and IReferenceService itself -- against the migrated,
					// seeded container above. No stub handler anywhere: this is the actual read path, over
					// a real database. Since the Midgard excision (NORSE071, 2026-08-03), the well is the
					// composition root's call to make -- and this fixture IS its own composition root, so
					// it makes the same call Program.cs does.
					services.AddNorseReferenceService(connectionString);
					services.AddWell<ReferenceDbContext>();

					// Mirrors MediatorParityTests: AuthorizationBehavior needs a principal to ask about,
					// and this suite has neither a circuit nor a cookie scheme to seed one for real.
					// ReferencePolicies.Public's permissive assertion means any principal satisfies it.
					services.AddScoped<IPrincipalAccessor>(_ => new ReferenceTestPrincipalAccessor(principal));
				});
				webHost.Configure(app =>
				{
					app.UseRouting();
					app.UseAuthorization();
					app.UseEndpoints(endpoints => endpoints.MapGrpcService<ReferenceService>());
				});
			})
			.StartAsync(cancellationToken);
	}

	static IReferenceService CreateWireClient(IHost host)
	{
		var channel = GrpcChannel.ForAddress("http://localhost",
			new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
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

		var outcome = await CreateWireClient(host).GetCountry(new() { CodeInput = code }, cancellationToken);

		var response = outcome.Match(static r => r,
			static p => throw new InvalidOperationException(p.Category.ToString()));

		// Recompute client-side from the frozen name form -- Guid equality AND canonical string
		// equality (byte-order settled law: this assertion is its only mention, per spec §1).
		DeterministicGuid local = new(ReferenceNamespaces.Iso3166, expectedD3);
		response.Id.ShouldBe(local.Value);
		response.Id.ToString().ShouldBe(local.Value.ToString());
	}

	[Fact]
	async Task Garbage_is_illegal_to_write_and_faults_client_side()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var host = await CreateHostAsync(fixture.ConnectionString, cancellationToken);

		// The wire-stamped request law (spec 2026-08-08-wire-stamped-request-scalars) plus the
		// success-unwrap law (../Glitnir/docs/Platform/specs/2026-08-02-result-success-unwrap-on-serialize-design.md):
		// a failed Result<T> is illegal to write, so "banana" never leaves this process --
		// ResultEnumSerializer<IsoCountryCode>.Write throws inside the client marshaller, the call
		// surfaces as a trailer-less RpcException, and DecodeProblem degrades it to the bare Fault.
		// The null CorrelationId is the proof the fault is client-minted: every server-side Fault
		// (ExceptionTranslationBehavior, UnhandledExceptionInterceptor) carries one by construction.
		var outcome = await CreateWireClient(host).GetCountry(new() { CodeInput = "banana" }, cancellationToken);

		outcome.TryGetValue(out Failed failed).ShouldBeTrue();
		failed.Problem.Category.ShouldBe(ErrorCategory.Fault);
		failed.Problem.CorrelationId.ShouldBeNull();
		failed.Problem.Errors.ShouldBeEmpty();
	}

	[Fact]
	async Task An_undefined_varint_maps_to_invalid_argument_on_the_wire()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var host = await CreateHostAsync(fixture.ConnectionString, cancellationToken);

		// The server's own verdict on the binary channel, which no typed client can author (a failed
		// stamp and an undefined success are both illegal to write) -- hand-built wire bytes, the same
		// surviving raw-bytes idiom as TriProtocolSwoopTests.EchoRawAsync: field 1 as varint 9999, a
		// value no M49 member defines. ResultEnumSerializer<IsoCountryCode>.Read stamps
		// Failure(Malformed, "9999") -- deserialization is the parse event; the server holds its own
		// verdict -- and the handler answers the typed validation failure, echoing the failed input.
		var outcome = await GetCountryRawAsync(host, [0x08, 0x8F, 0x4E], cancellationToken);

		outcome.TryGetValue(out Failed failed).ShouldBeTrue();
		failed.Problem.Category.ShouldBe(ErrorCategory.Validation);
		failed.Problem.Errors["code"].ShouldContain("9999");
	}

	/// <summary>
	///     Invokes <see cref="IReferenceService.GetCountry" /> with hand-built wire bytes for the request,
	///     decoding <see cref="Outcome{T}" /> failures via <see cref="OutcomeClientInterceptor" /> exactly as
	///     <see cref="CreateWireClient" />'s type-safe proxy would -- the raw-bytes idiom
	///     <c>TriProtocolSwoopTests.EchoRawAsync</c> established for wire states no client proxy can express
	///     by construction.
	/// </summary>
	static async Task<Outcome<CountryResponse>> GetCountryRawAsync(IHost host, byte[] requestBytes,
		CancellationToken cancellationToken)
	{
		var channel = GrpcChannel.ForAddress("http://localhost",
			new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
		var invoker = channel.Intercept(new OutcomeClientInterceptor());

		Method<byte[], Outcome<CountryResponse>> method = new(
			MethodType.Unary,
			serviceName: "grpc.reference.v1.ReferenceService",
			name: "GetCountry",
			requestMarshaller: Marshallers.Create<byte[]>(static bytes => bytes, static bytes => bytes),
			responseMarshaller: Marshallers.Create<Outcome<CountryResponse>>(
				serializer: static _ =>
					throw new NotSupportedException(
						$"{nameof(GetCountryRawAsync)} never serializes a response — this marshaller direction is client-inbound only."),
				deserializer: static bytes =>
				{
					using MemoryStream stream = new(bytes);
					return (Outcome<CountryResponse>)RuntimeTypeModel.Default.Deserialize(stream, null,
						typeof(Outcome<CountryResponse>))!;
				}));

		using var call = invoker.AsyncUnaryCall(method, host: null,
			new CallOptions(cancellationToken: cancellationToken), requestBytes);
		return await call.ResponseAsync;
	}
}

/// <summary>
///     Overrides <c>AddNorsePipeline()</c>'s own <see cref="IPrincipalAccessor" /> registration (DI resolves
///     the last registration for a single-service ask) so <c>AuthorizationBehavior</c> always has a
///     principal to ask about -- mirrors <c>MediatorParityTests.ReferenceTestPrincipalAccessor</c> exactly.
/// </summary>
sealed class ReferenceTestPrincipalAccessor(ClaimsPrincipal principal) : IPrincipalAccessor
{
	public ValueTask<ClaimsPrincipal> GetPrincipalAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(principal);
}
