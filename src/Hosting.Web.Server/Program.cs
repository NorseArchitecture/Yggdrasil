using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Norse.Abstractions.Components.Primitives;
using Norse.AuthN.Components;
using Norse.AuthN.Components.FluentUI;
using Norse.AuthN.Services;
using Norse.Hosting.Web.Components;
using Norse.Hosting.Web.Server;
using Norse.Hosting.Web.Server.Components;
using Norse.Identity.Web.Server;
using Norse.Identity.Web.Server.Components.Pages;
using Norse.Infrastructure.Components.Theme.FluentUI;
using Norse.Infrastructure.Web.Server.DeferredSignIn;
using Norse.Infrastructure.Web.Server.Mediator.Grpc;

Console.Title = "Norse Web Server";
var builder = WebApplication.CreateBuilder(args);

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
	.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

// AuthN.Public is satisfied by any principal, anonymous-role cookie included (Norse.AuthN.Services.AuthNPolicies) —
// every AuthenticationService method still declares it per decided law item 4, so it must exist here even though
// it imposes no real requirement. Composition root's job: Heimdall stays policy-name-only, never registers policies.
builder.Services.AddAuthorizationBuilder()
	.AddPolicy(AuthNPolicies.Public, policy => policy.RequireAssertion(_ => true));

var norseIdentityConnectionString = builder.Configuration.GetConnectionString("norse_identity")
	?? throw new InvalidOperationException("Connection string 'norse_identity' is not configured.");
builder.Services
	.AddSingleton<IEmailSender<NorseUser>, IdentityNoOpEmailSender>()
	.AddNorseCodeFirstGrpc() // generic, per Midgard — knows nothing about AuthenticationService specifically
	.AddNorseAuthenticationService(norseIdentityConnectionString)
	.AddScoped<IAuthenticationGateway, AuthenticationInProcessGateway>() // generated, Task 8/9
	.AddScoped<EnvelopeHydrationState>()
	.AddDeferredSignIn()
	// Dev-only: lets Postman/grpcurl discover IAuthenticationService and call it directly, proving the
	// protobuf-net.Grpc wire lifecycle independent of the Blazor UI. Never mapped outside Development —
	// reflection hands out the full service/message catalog to anyone who can reach the endpoint.
	.AddGrpcReflection();

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

app.MapStaticAssets();

app.MapRazorComponents<App>()
	.AddInteractiveServerRenderMode()
	.AddInteractiveWebAssemblyRenderMode()
	.AddAdditionalAssemblies(typeof(Routes).Assembly, typeof(Login).Assembly, typeof(Logout).Assembly, typeof(ExternalLogin).Assembly);

app.MapAdditionalIdentityEndpoints();

app.MapGrpcService<AuthenticationService>();
app.MapDeferredSignIn();

if (app.Environment.IsDevelopment())
{
	app.MapGrpcReflectionService();
}

await app
	.RunAsync()
	.ConfigureAwait(false);
