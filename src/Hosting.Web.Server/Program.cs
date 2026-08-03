using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Norse.Abstractions.Components.Primitives;
using Norse.AuthN.Components;
using Norse.AuthN.Components.FluentUI;
using Norse.AuthN.Services;
using Norse.Hosting.Web.Components;
using Norse.Hosting.Web.Server;
using Norse.Hosting.Web.Server.Components;
using Norse.Hosting.Web.Server.NorseXmlShapes;
using Norse.Identity.Web.Server;
using Norse.Identity.Web.Server.Components.Pages;
using Norse.Infrastructure.Components.Theme.FluentUI;
using Norse.Infrastructure.Serialization;
using Norse.Infrastructure.ServiceDefaults.AspNet;
using Norse.Infrastructure.Web.Server.DeferredSignIn;
using Norse.Infrastructure.Web.Server.Json;
using Norse.Infrastructure.Web.Server.Mediator;
using Norse.Infrastructure.Web.Server.Mediator.Grpc;
using Norse.Infrastructure.Web.Server.OpenApi;
using Norse.Infrastructure.Web.Server.Xml;
using Norse.Reference;
using Norse.Reference.Web.Server;
using ProtoBuf.Grpc.Server;

Console.Title = "Norse Web Server";
var builder = WebApplication.CreateBuilder(args);
builder.AddAspNetServiceDefaults();

builder.Services
	.AddRazorComponents()
	.AddInteractiveServerComponents()
	.AddInteractiveWebAssemblyComponents();

// Logout lives in AuthN.Components (headless -- no FluentUI markup); Login/Register stay in
// AuthN.Components.FluentUI; the Account pages (ExternalLogin, Manage, etc.) live in Himinbjorg's
// Identity.Web.Server. Three distinct assemblies, all need to be discoverable by the router.
builder.Services
	.AddSingleton(new RoutesAdditionalAssemblies([typeof(Program).Assembly, typeof(Login).Assembly, typeof(Logout).Assembly, typeof(ExternalLogin).Assembly]))
	.AddSingleton<IAppShellLayout, AppShellLayout>()
	.AddNorseFluentUiTheme()
	.AddCascadingAuthenticationState()
	.AddScoped<IdentityRedirectManager>()
	.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>()
	.AddScoped<CircuitHandler, LoggingCircuitHandler>();

// AuthN.Public/Reference.Public are satisfied by any principal, anonymous-role cookie included
// (Norse.AuthN.Services.AuthNPolicies / Norse.Reference.ReferencePolicies) — every
// AuthenticationService/ReferenceService method still declares one per decided law item 4 (NORSE011),
// so both must exist here even though neither imposes a real requirement. Composition root's job:
// Heimdall/Mimir stay policy-name-only, never register policies themselves.
builder.Services.AddAuthorizationBuilder()
	.AddPolicy(AuthNPolicies.Public, policy => policy.RequireAssertion(_ => true))
	.AddPolicy(ReferencePolicies.Public, policy => policy.RequireAssertion(_ => true));

var norseReferenceConnectionString = builder.Configuration.GetConnectionString("norse_reference")
	?? throw new InvalidOperationException("Connection string 'norse_reference' is not configured.");
builder
	.AddNorseAuthenticationService("norse_identity")
	.Services
	.AddNorseReferenceService(norseReferenceConnectionString)
	.AddNorsePipeline() // Midgard: behaviors in law order, PrincipalAccessor, Sender
	.AddNorseCodeFirstGrpc() // Midgard: Unhandled -> Seeding -> Outcome interceptor stack
	.AddNorseSerialization() // Midgard: JSON/XML serialization and content negotiation
	.AddDeferredSignIn()
	// Dev-only: lets Postman/grpcurl discover IAuthenticationService and call it directly, proving the
	// protobuf-net.Grpc wire lifecycle independent of the Blazor UI. Never mapped outside Development —
	// reflection hands out the full service/message catalog to anyone who can reach the endpoint.
	.AddCodeFirstGrpcReflection();

// Futhark's REST ambassador layer (../Glitnir/docs/Platform/specs/2026-08-01-opinionated-xml-serialization-design.md):
// content-negotiated JSON/XML for hand-authored GrpcControllerBase facade controllers, plus the OpenAPI
// document those same controllers author. OutcomeServerInterceptor sat designed, tested, and unwired
// for a full release before the platform's own audit caught it — this is the line where that mistake
// does not repeat for the REST fold and the two OpenAPI union-unwrap transformers (spec §10.4).
builder.Services
	.AddControllers()
	.AddNorseJson(NorseEnumNameRegistration.Build())
	.AddNorseXml(XmlCaseStyle.CamelCase, NorseXmlShapeRegistration.Build());
builder.Services.AddOpenApi(options =>
{
	options.AddSchemaTransformer<ResultSchemaTransformer>();
	options.AddSchemaTransformer<EnumSchemaTransformer>();
	options.AddSchemaTransformer<XmlMetadataTransformer>();
	options.AddDocumentTransformer<UnionLeakGuardTransformer>();
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
	app.UseWebAssemblyDebugging();
}
else
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
	.AddAdditionalAssemblies(typeof(Routes).Assembly, typeof(Login).Assembly, typeof(Logout).Assembly, typeof(ExternalLogin).Assembly);

app.MapAdditionalIdentityEndpoints();

app.MapControllers();
app.MapOpenApi();

app.MapNorseGrpcServices();
app.MapDefaultEndpoints();
// The gRPC health service is polled on a timer by its own clients, as aggressively as any HTTP
// probe, and it is an endpoint this project does not own -- so its metrics are suppressed here, at
// the map site. Its traces need nothing: AspNetTraceFilter already excludes the /grpc.health. prefix.
app.MapGrpcHealthChecksService().DisableHttpMetrics();
app.MapDeferredSignIn();

if (app.Environment.IsDevelopment())
{
	app.MapCodeFirstGrpcReflectionService();
}

await app
	.RunAsync()
	.ConfigureAwait(false);
