using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Norse.AuthN.Components;
using Norse.Hosting.Web.Client;
using Norse.Hosting.Web.Components;
using Norse.Infrastructure.Components.Theme.FluentUI;
using ProtoBuf.Grpc.Client;

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

// This project hosts no components of its own (see the architecture note above), so it has
// no additional assemblies to contribute beyond Routes' own — unlike Hosting.Web.Server, which
// contributes its own assembly for the server-only Identity/Account pages.
builder.Services.AddSingleton(new RoutesAdditionalAssemblies([]));

// gRPC-Web rides ordinary HTTP/1.1 — no HTTP/2-specific channel configuration needed in the browser.
var authNChannel = GrpcChannel.ForAddress(builder.HostEnvironment.BaseAddress, new GrpcChannelOptions
{
	HttpHandler = new GrpcWebHandler { InnerHandler = new BrowserCredentialsHandler { InnerHandler = new HttpClientHandler() } },
});
builder.Services.AddSingleton(authNChannel.CreateGrpcService<IAuthenticationService>());
builder.Services.AddScoped<IAuthenticationGateway, WasmAuthenticationGateway>();

await builder
	.Build()
	.RunAsync()
	.ConfigureAwait(false);
