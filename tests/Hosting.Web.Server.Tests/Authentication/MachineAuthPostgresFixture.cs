using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Norse.Hosting.Web.Server.Tests.NorseXmlShapes;
using Norse.Identity.EntityFramework;
using Norse.Identity.Migrations;
using Norse.Identity.Migrations.PostgreSQL;
using Norse.Identity.Web.Server;
using Norse.Infrastructure.Backend.Keys;
using Norse.Infrastructure.Persistence.EntityFramework;
using Norse.Infrastructure.Web.Server.Authentication;
using Norse.Infrastructure.Web.Server.Json;
using Norse.Infrastructure.Web.Server.Mediator;
using Norse.Infrastructure.Web.Server.Xml;
using Norse.Persistence.EntityFramework;
using Norse.Persistence.EntityFramework.PostgreSQL;
using Norse.Reference.Data.EntityFramework;
using Norse.Reference.Data.EntityFramework.Migrations;
using Norse.Reference.Data.EntityFramework.Migrations.PostgreSQL;
using Norse.Reference.Web.Server;
using Testcontainers.PostgreSql;

namespace Norse.Hosting.Web.Server.Tests.Authentication;

/// <summary>
///     Two real Postgres containers (identity + reference), migrated and seeded through the exact same
///     contributors the migrations service runs, standing behind a real bespoke <see cref="TestServer" />
///     host wired from the same production DI extensions <c>Program.cs</c> calls — the
///     <see cref="Norse.Hosting.Web.Server.Tests.CountryLookupE2ETests" /> "own composition root" pattern
///     (confirmed by reading that fixture during planning), extended to also cover identity/OpenIddict
///     issuance, which <c>Program.cs</c>'s pre-<c>Build()</c> configuration reads make impossible to test
///     through <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}" />.
/// </summary>
public sealed class MachineAuthPostgresFixture : IAsyncLifetime
{
	readonly PostgreSqlContainer _identityContainer = new PostgreSqlBuilder("postgres:19beta2")
		.WithDatabase("norse_identity")
		.Build();

	readonly PostgreSqlContainer _referenceContainer = new PostgreSqlBuilder("postgres:19beta2")
		.WithDatabase("norse_reference")
		.Build();

	X509Certificate2 _certificate = null!;
	string _keysRoot = null!;

	public string IdentityConnectionString { get; private set; } = null!;
	public string ReferenceConnectionString { get; private set; } = null!;

	public async ValueTask InitializeAsync()
	{
		await Task.WhenAll(_identityContainer.StartAsync(), _referenceContainer.StartAsync());
		IdentityConnectionString = _identityContainer.GetConnectionString();
		ReferenceConnectionString = _referenceContainer.GetConnectionString();
		_certificate = MachineAuthTestCertificate.CreateFresh();
		// One directory, reused by every host CreateHostAsync builds -- same rationale as
		// PostgresIdentityFixture's own "exactly one instance" warning: EF's ModelSource cache keys the
		// compiled model by an options fingerprint that does not include the connection string, so two
		// hosts in this process could otherwise share a cached model built against one host's key seam
		// while believing they registered their own. Every host this fixture builds points at the same
		// connection string anyway (one container), so one shared keys directory is both correct and safe.
		_keysRoot = Path.Combine(Path.GetTempPath(), $"norse-identity-keys-{Guid.NewGuid():N}");

		DbContextOptionsBuilder<NorseIdentityDbContext> identityOptions = new();
		identityOptions.ApplyNorseProviderOptions(NorsePostgresEfProvider.Instance,
			IdentityConnectionString, typeof(NorseIdentityDbContextFactory).Assembly.GetName().Name);
		await using (NorseIdentityDbContext identityContext = new(identityOptions.Options))
			await new NorseIdentityMigrationContributor(identityContext).MigrateAsync(CancellationToken.None);

		DbContextOptionsBuilder<ReferenceDbContext> referenceOptions = new();
		referenceOptions.ApplyNorseProviderOptions(NorsePostgresEfProvider.Instance,
			ReferenceConnectionString, typeof(ReferenceDbContextFactory).Assembly.GetName().Name);
		await using ReferenceDbContext referenceContext = new(referenceOptions.Options);
		await new NorseReferenceMigrationContributor(referenceContext).MigrateAsync(CancellationToken.None);
		await new ReferenceDataSeedContributor(referenceContext).SeedAsync(CancellationToken.None);
	}

	public async ValueTask DisposeAsync()
	{
		_certificate.Dispose();
		await Task.WhenAll(_identityContainer.DisposeAsync().AsTask(), _referenceContainer.DisposeAsync().AsTask());
		if (Directory.Exists(_keysRoot))
			Directory.Delete(_keysRoot, recursive: true);
	}

	/// <summary>
	///     Boots a fresh, real <see cref="WebApplication" /> against this fixture's two containers, wiring
	///     the exact production DI extensions <c>Program.cs</c> calls for identity issuance/validation and
	///     the reference facade — same shape as <c>CountryLookupE2ETests.CreateHostAsync</c>, extended to
	///     cover OpenIddict.
	/// </summary>
	/// <param name="accessTokenLifetime">
	///     Overrides the issued access token's lifetime, for the expired-token test — omitted everywhere
	///     else, matching OpenIddict's own default.
	/// </param>
	public async Task<WebApplication> CreateHostAsync(TimeSpan? accessTokenLifetime = null)
	{
		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Configuration["ConnectionStrings:identity"] = IdentityConnectionString;
		builder.AddNorseAuthenticationService("identity", _certificate, accessTokenLifetime);
		builder.Services
			.AddNorseDevelopmentKeys(_keysRoot)
			.AddNorseReferenceService(ReferenceConnectionString)
			.AddWell<ReferenceDbContext>()
			.AddNorsePipeline();
		// Separate statements, matching Program.cs: AddNorseAuthentication() returns AuthenticationBuilder,
		// not IServiceCollection, so it cannot chain into AddNorsePolicies()/AddControllers().
		builder.Services.AddNorseAuthentication();
		builder.Services.AddNorsePolicies();
		builder.Services
			.AddControllers(options => options.ReturnHttpNotAcceptable = true)
			// Default ApplicationPartManager discovery walks from the process's entry assembly -- the
			// real Program.cs IS Hosting.Web.Server.dll, which references Reference.Web.Server directly,
			// so CountriesController is found for free there. This fixture's entry assembly is the test
			// host process instead, one hop further from Reference.Web.Server, so the part carrying
			// CountriesController needs stating explicitly rather than relying on that walk.
			.AddApplicationPart(typeof(CountriesController).Assembly)
			.AddNorseJson(NorseEnumNameRegistration.Build())
			.AddNorseXml(XmlCaseStyle.CamelCase, NorseXmlShapeRegistration.Build());

		var app = builder.Build();
		app.UseAuthentication();
		app.UseAuthorization();
		app.MapControllers();
		app.MapNorseOpenIddictEndpoints();

		await app.StartAsync(CancellationToken.None);
		return app;
	}
}
