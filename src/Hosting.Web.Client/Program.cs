using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
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

// This project hosts no components of its own (see the architecture note above), so it has
// no additional assemblies to contribute beyond Routes' own — unlike Hosting.Web.Server, which
// contributes its own assembly for the server-only Identity/Account pages.
builder.Services.AddSingleton(new RoutesAdditionalAssemblies([]));

await builder
	.Build()
	.RunAsync()
	.ConfigureAwait(false);
