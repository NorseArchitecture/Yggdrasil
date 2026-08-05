using FluentValidation;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Norse.AuthN.Components;
using Norse.AuthN.Components.FluentUI;
using Norse.AuthN.Services;
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

// Heimdall's wire-request validators, registered for the WASM circuit so Blazilla's FluentValidator
// resolves them client-side — the same classes the server runs again through the generated
// CommandRequestValidator adapter (single source of validation truth, run twice by design).
builder.Services
	.AddScoped<IValidator<LoginRequest>, LoginRequestValidator>()
	.AddScoped<IValidator<RegisterRequest>, RegisterRequestValidator>()
	.AddScoped<IValidator<GetMaskedPersonalDataRequest>, GetMaskedPersonalDataRequestValidator>();

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
//
// KNOWN BROKEN ON net11 PREVIEW WASM (root-caused 2026-08-05): Grpc.Net.Client's BalancerHttpHandler/
// Subchannel.ConnectTransportAsync calls SemaphoreSlim.Wait(0) on first connect; the net11-preview
// single-threaded WASM runtime throws PlatformNotSupportedException from that non-blocking try-acquire
// (guard sits ahead of the acquire path — dotnet/runtime SemaphoreSlim.WaitCore), the exception dies
// inside grpc-dotnet's fire-and-forget connect task, and every call parks forever without dispatching.
// The identical stack (same packages) works on desktop .NET, and a raw gRPC-Web POST via plain
// HttpClient works from this very WASM runtime — the fault is exclusively this library/runtime pairing.
var norseChannel = GrpcChannel.ForAddress(builder.HostEnvironment.BaseAddress, new GrpcChannelOptions
{
	HttpHandler = new GrpcWebHandler { InnerHandler = new BrowserCredentialsHandler { InnerHandler = new HttpClientHandler() } },
});
builder.Services.AddNorseGrpcClients(norseChannel); // generated, Task 14

await builder
	.Build()
	.RunAsync()
	.ConfigureAwait(false);
