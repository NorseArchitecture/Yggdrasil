using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Norse.Abstractions.Components.Primitives;
using Norse.AuthN.Components;
using Norse.AuthN.Components.FluentUI;
using Norse.Hosting.Web.Components;
using Norse.Hosting.Web.Server;
using Norse.Hosting.Web.Server.Components;
using Norse.Identity;
using Norse.Identity.Web.Server;
using Norse.Identity.Web.Server.Components.Pages;
using Norse.Infrastructure.Components.Theme.FluentUI;
using Norse.Infrastructure.Web.Server.DeferredSignIn;

Console.Title = "Norse Web Server";
var builder = WebApplication.CreateBuilder(args);

builder.Services
	.AddRazorComponents()
	.AddInteractiveServerComponents()
	.AddInteractiveWebAssemblyComponents();

// Logout lives in AuthN.Components (headless -- no FluentUI markup); Login/Register stay in
// AuthN.Components.FluentUI; the Account pages (ExternalLogin, Manage, etc.) live in Himinbjorg's
// Identity.Web.Server. Three distinct assemblies, all need to be discoverable by the router.
builder.Services.AddSingleton(new RoutesAdditionalAssemblies([typeof(Program).Assembly, typeof(Login).Assembly, typeof(Logout).Assembly, typeof(ExternalLogin).Assembly]));
builder.Services.AddSingleton<IAppShellLayout, AppShellLayout>();
builder.Services.AddNorseFluentUiTheme();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthorization();
builder.Services.AddSingleton<IEmailSender<NorseUser>, IdentityNoOpEmailSender>();

var norseIdentityConnectionString = builder.Configuration.GetConnectionString("norse_identity")
	?? throw new InvalidOperationException("Connection string 'norse_identity' is not configured.");
builder.Services.AddNorseAuthenticationService(norseIdentityConnectionString);
builder.Services.AddScoped<IAuthenticationGateway, BlazorServerAuthenticationGateway>();
builder.Services.AddDeferredSignIn();

// Dev-only: lets Postman/grpcurl discover IAuthenticationService and call it directly, proving the
// protobuf-net.Grpc wire lifecycle independent of the Blazor UI. Never mapped outside Development —
// reflection hands out the full service/message catalog to anyone who can reach the endpoint.
builder.Services.AddGrpcReflection();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
	app.UseWebAssemblyDebugging();
}
else
{
	app.UseExceptionHandler("/Error", createScopeForErrors: true);
	app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
	.AddInteractiveServerRenderMode()
	.AddInteractiveWebAssemblyRenderMode()
	.AddAdditionalAssemblies(typeof(Routes).Assembly, typeof(Login).Assembly, typeof(Logout).Assembly, typeof(ExternalLogin).Assembly);

app.MapAdditionalIdentityEndpoints();

app.MapNorseAuthenticationService();
app.MapDeferredSignIn();

if (app.Environment.IsDevelopment())
{
	app.MapGrpcReflectionService();
}

await app
	.RunAsync()
	.ConfigureAwait(false);
