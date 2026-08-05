using FluentValidation;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Norse.AuthN.Components;
using Norse.AuthN.Components.FluentUI;
using Norse.AuthN.Services;
using Norse.Hosting.Web.Client;
using Norse.Hosting.Web.Components;
using Norse.Infrastructure.Components.Theme.FluentUI;
using Norse.Infrastructure.Web.Client.Grpc;

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

// gRPC-Web rides ordinary HTTP/1.1 — no HTTP/2-specific configuration needed in the browser.
// One invoker, not one per service: every Norse gRPC service this client talks to (IAuthenticationService,
// IIdentityService, IReferenceService) is hosted in the same Hosting.Web.Server process at the same base
// address, and AddNorseGrpcClients (Midgard's generated client wiring) registers a proxy for every
// contract it discovers in this compilation over the single invoker handed to it.
//
// GrpcWebCallInvoker (Midgard) instead of GrpcChannel, deliberately: Grpc.Net.Client's
// BalancerHttpHandler/Subchannel connect path performs a SemaphoreSlim.Wait(0) that the net11-preview
// single-threaded WASM runtime rejects with PlatformNotSupportedException inside a fire-and-forget
// task — every channel-based call parks forever without dispatching (root-caused 2026-08-05).
// EXIT CONDITION: at each .NET 11 preview/RC/GA bump and each GrpcVersion bump, swap this back to
// GrpcChannel.ForAddress(...).CreateCallInvoker() and run the Playwright smoke — the day it
// dispatches, delete GrpcWebCallInvoker and this note.
#pragma warning disable CA2000 // The invoker owns the HttpClient for the application's lifetime — WASM hosts never tear it down.
GrpcWebCallInvoker norseInvoker = new(new HttpClient(new BrowserCredentialsHandler { InnerHandler = new HttpClientHandler() })
{
	BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});
#pragma warning restore CA2000
builder.Services.AddNorseGrpcClients(norseInvoker); // generated, Task 14

await builder
	.Build()
	.RunAsync()
	.ConfigureAwait(false);
