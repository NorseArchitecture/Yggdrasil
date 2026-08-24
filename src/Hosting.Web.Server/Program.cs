using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Norse.Abstractions.Components.Primitives;
using Norse.AuthN.Components;
using Norse.AuthN.Services;
using Norse.Hosting.Web.Components;
using Norse.Hosting.Web.Server;
using Norse.Hosting.Web.Server.Components;
using Norse.Hosting.Web.Server.NorseXmlShapes;
using Norse.Hosting.Web.Server.OpenApi;
using Norse.Identity.Web.Server;
using Norse.Infrastructure.Backend.Keys;
using Norse.Infrastructure.Backend.Serialization;
using Norse.Infrastructure.Components.Theme.FluentUI;
using Norse.Infrastructure.Persistence.EntityFramework;
using Norse.Infrastructure.ServiceDefaults.AspNet;
using Norse.Infrastructure.Web.Server.Authentication;
using Norse.Infrastructure.Web.Server.DeferredSignIn;
using Norse.Infrastructure.Web.Server.Json;
using Norse.Infrastructure.Web.Server.Mediator;
using Norse.Infrastructure.Web.Server.Mediator.Grpc;
using Norse.Infrastructure.Web.Server.OpenApi;
using Norse.Infrastructure.Web.Server.Xml;
using Norse.Reference.Data.EntityFramework;
using Norse.Reference.Web.Server;
using ProtoBuf.Grpc.Server;
using Scalar.AspNetCore;

Console.Title = "Norse Web Server";
var builder = WebApplication.CreateBuilder(args);
builder.AddAspNetServiceDefaults();
builder.Services
	.AddRazorComponents()
	.AddInteractiveServerComponents()
	.AddInteractiveWebAssemblyComponents()
	.AddAuthenticationStateSerialization();

// AddNorseClientComponents() (Midgard's generated server-side discovery, Task 14) replaces the
// hand-rolled RoutesAdditionalAssemblies singleton this block used to carry: Logout (AuthN.Components,
// headless), Login/Register (AuthN.Components.FluentUI), and the Account pages (ExternalLogin, Manage,
// etc., Himinbjorg's Identity.Web.Server) are all discovered at compile time from what this project
// actually references -- adding a validator or a routed component anywhere upstream needs no edit
// here anymore. The endpoint half of composition (AddAdditionalAssemblies) is chained onto
// MapRazorComponents<App>() below via the generated AddNorseComponentAssemblies().
builder.Services
	.AddNorseClientComponents()
	.AddNorseSessionTransition()
	.AddSingleton<IAppShellLayout, AppShellLayout>()
	.AddNorseFluentUiTheme()
	.AddCascadingAuthenticationState()
	.AddScoped<IdentityRedirectManager>()
	.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>()
	.AddScoped<CircuitHandler, LoggingCircuitHandler>();

var norseReferenceConnectionString = builder.Configuration.GetConnectionString("norse_reference")
	?? throw new InvalidOperationException("Connection string 'norse_reference' is not configured.");
var oidcSigningCertPfx = builder.Configuration["OIDC_SIGNING_CERT_PFX"]
	?? throw new InvalidOperationException("Configuration value 'OIDC_SIGNING_CERT_PFX' is not configured.");
var oidcSigningCertificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(oidcSigningCertPfx), password: null);
builder
	.AddNorseAuthenticationService("norse_identity", oidcSigningCertificate)
	.Services
	// Dev-grade only: rooted content-root-relative so subject identities survive process restarts
	// without ever naming a machine-absolute path. The production seam is a vault-backed provider.
	.AddNorseDevelopmentKeys(Path.Combine(builder.Environment.ContentRootPath, "norse-dev-keys"))
	.AddNorseReferenceService(norseReferenceConnectionString)
	// Mímir stays Midgard-blind (NORSE071 remediation): the well itself --
	// IReadRepository<CountryOrAreaView> -- is the composition root's own call, not Mímir's.
	.AddWell<ReferenceDbContext>()
	.AddNorsePipeline() // Midgard: behaviors in law order, PrincipalAccessor, Sender
	.AddNorseCodeFirstGrpc() // Midgard: Unhandled -> Seeding -> Outcome interceptor stack
	.AddNorseSerialization() // Midgard: the serialization seam — naming-strategy-keyed ISerializerProvider (STJ-backed)
	.AddDeferredSignIn()
	// Dev-only: lets Postman/grpcurl discover IAuthenticationService and call it directly, proving the
	// protobuf-net.Grpc wire lifecycle independent of the Blazor UI. Never mapped outside Development —
	// reflection hands out the full service/message catalog to anyone who can reach the endpoint.
	.AddCodeFirstGrpcReflection();

// Lane composition (Glitnir Platform/specs/2026-08-21-principal-at-the-door-design.md §2.2). No ambient
// default scheme: the selector reads endpoint shape and forwards to exactly one lane, so an endpoint that
// declares nothing gets no principal rather than the wrong one. Placed after AddNorseAuthenticationService
// above (which calls AddIdentity<NorseUser, NorseRole>() internally) deliberately, not where the block it
// replaces used to sit: AddIdentity's own AddAuthentication call sets AuthenticationOptions.DefaultScheme
// itself, and Options composition is last-registration-wins per property, so AddNorseAuthentication() must
// run after it to have its own DefaultScheme/DefaultAuthenticateScheme/DefaultChallengeScheme/
// DefaultForbidScheme choices win (Midgard's AddNorseAuthentication doc comment states the requirement).
builder.Services.AddNorseAuthentication();

// Every [NorsePolicy] declaration in the resolved reference set, registered once. This call replaces four
// hand-written AddPolicy lambdas -- adding a policy anywhere upstream now needs no edit here, which is the
// point: a fifth hand-rolled registration is what guaranteed the argument would be had again.
builder.Services.AddNorsePolicies();

// Futhark's REST ambassador layer (../Glitnir/docs/Platform/specs/2026-08-01-opinionated-xml-serialization-design.md):
// content-negotiated JSON/XML for hand-authored GrpcControllerBase facade controllers, plus the OpenAPI
// document those same controllers author. OutcomeServerInterceptor sat designed, tested, and unwired
// for a full release before the platform's own audit caught it — this is the line where that mistake
// does not repeat for the REST fold and the two OpenAPI union-unwrap transformers (spec §10.4).
builder.Services
	// No silent fallbacks on the negotiation seam: an Accept header naming neither JSON nor XML gets
	// an honest 406, never the first formatter's best guess. (MVC-only — the gRPC leg negotiates
	// nothing; protobuf rides its own routes.)
	.AddControllers(options => options.ReturnHttpNotAcceptable = true)
	.AddNorseJson(NorseEnumNameRegistration.Build())
	.AddNorseXml(XmlCaseStyle.CamelCase, NorseXmlShapeRegistration.Build());
builder.Services.AddOpenApi(options =>
{
	options.AddSchemaTransformer<ResultSchemaTransformer>();
	options.AddSchemaTransformer<EnumSchemaTransformer>();
	options.AddSchemaTransformer<XmlMetadataTransformer>();
	options.AddDocumentTransformer<UnionLeakGuardTransformer>();
	options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
	options.AddOperationTransformer<StandardResponsesTransformer>();
	options.AddOperationTransformer<MachineAuthOperationTransformer>();
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
	app
		.UseExceptionHandler("/Error", createScopeForErrors: true)
		.UseHsts();
}

app
	.UseHttpsRedirection()
	.UseAuthentication()
	.UseAuthorization()
	.UseAntiforgery();

app.MapStaticAssets().DisableHttpMetrics();

app.MapRazorComponents<App>()
	.AddInteractiveServerRenderMode()
	.AddInteractiveWebAssemblyRenderMode()
	.AddNorseComponentAssemblies();

app.MapAdditionalIdentityEndpoints();
app.MapNorseOpenIddictEndpoints();

app.MapControllers();
app.MapOpenApi();

app.MapNorseGrpcServices();
app.MapDefaultEndpoints();
// The gRPC health service is polled on a timer by its own clients, as aggressively as any HTTP
// probe, and it is an endpoint this project does not own -- so its metrics are suppressed here, at
// the map site. Its traces need nothing: AspNetTraceFilter already excludes the /grpc.health. prefix.
app.MapGrpcHealthChecksService().DisableHttpMetrics();
app.MapDeferredSignIn();

// Test-only probe pinned in production code deliberately (Task 15, principal-at-the-door,
// ../Glitnir/docs/Platform/plans/2026-08-21-principal-at-the-door.md): ChallengeAndForbidTests needs a
// real browser-lane endpoint guarded by a policy an anonymous principal genuinely fails, to pin that the
// composed host answers forbid with a bare 403 and never a login redirect. IdentityPolicies.Self would not
// do -- RequireAuthenticatedUser() is satisfied by the anonymous principal itself (spec §2.4: anonymous is
// authenticated) -- so MaskedDisclosure (RequireRole(IdentityPolicies.SystemRole)) is used instead: the
// anonymous role is never SystemRole.
app.MapGet("/protected-probe", () => Results.Ok())
	.RequireAuthorization(IdentityPolicies.MaskedDisclosure)
	.ExcludeFromDescription();

if (app.Environment.IsDevelopment())
{
	app.MapCodeFirstGrpcReflectionService();
	// The human-readable face of MapOpenApi's document -- dev-only, same posture as gRPC reflection:
	// discovery surfaces are for developers at the bench, never the deployed footprint.
	app.MapScalarApiReference();
}

await app
	.RunAsync()
	.ConfigureAwait(false);
