using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Norse.AuthN.Components.FluentUI;
using Norse.Hosting.Web.Client;
using Norse.Hosting.Web.Components;
using Norse.Infrastructure.Components.Theme.FluentUI;

// <summary>
// ARCHITECTURE NOTE — READ BEFORE ADDING CODE HERE
//
// This project (Microsoft.NET.Sdk.BlazorWebAssembly) is a WASM host shell ONLY.
// Do not add components, pages, services, or business logic to this project.
// The only thing that belongs in Program.cs (and this project generally) is
// dependency injection wire-up — registering services, configuring the host,
// and bootstrapping the app.
//
// Components go in one of two places instead:
//   - Norse.Hosting.Web.Server       -> components with server-side dependencies
//   - Norse.Hosting.Web.Components   -> components with no server-side dependencies
//
// If you're about to drop a .razor file, a service implementation, or any
// non-DI logic into this project, stop and move it to the correct project
// above instead.
// </summary>
var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services
	.AddAuthorizationCore()
	.AddCascadingAuthenticationState()
	.AddAuthenticationStateDeserialization()
	.AddNorseFluentUiTheme();

// This project hosts no components of its own (see the architecture note above), but it does
// reference Heimdall's AuthN.Components.FluentUI (Login/Register) — those pages ship inside this
// WASM binary, so the client-side Router needs to be told about that assembly too, or InteractiveAuto's
// hand-off from the server circuit to WASM can't resolve /Account/Login and falls back to NotFound.
// Logout (AuthN.Components, headless) and ExternalLogin/Manage (Himinbjorg's Identity.Web.Server) stay
// excluded — neither assembly is referenced here, sealed server-side per Himinbjorg's own CLAUDE.md.
builder.Services.AddSingleton(new RoutesAdditionalAssemblies([typeof(Login).Assembly]));

// gRPC-Web rides ordinary HTTP/1.1 — no HTTP/2-specific channel configuration needed in the browser.
// One channel, not one per service: every Norse gRPC service this client talks to (IAuthenticationService,
// IReferenceService) is hosted in the same Hosting.Web.Server process at the same base address, and
// AddNorseGrpcClients (Midgard's generated client wiring) registers a proxy for every contract it
// discovers in this compilation over the single channel handed to it — there is no per-contract channel
// parameter to plumb.
var norseChannel = GrpcChannel.ForAddress(builder.HostEnvironment.BaseAddress, new GrpcChannelOptions
{
	HttpHandler = new GrpcWebHandler { InnerHandler = new BrowserCredentialsHandler { InnerHandler = new HttpClientHandler() } },
});
builder.Services.AddNorseGrpcClients(norseChannel); // generated, Task 14

await builder
	.Build()
	.RunAsync()
	.ConfigureAwait(false);
