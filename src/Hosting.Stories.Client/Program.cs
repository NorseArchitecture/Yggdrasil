using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Norse.AuthN.Services;
using Norse.Hosting.Stories.Client;
using Norse.Infrastructure.Components.Theme.FluentUI;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services
	.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) })
	.AddNorseFluentUiTheme()
	.AddScoped<IAuthenticationGateway, FakeAuthenticationGateway>();

await builder.Build().RunAsync().ConfigureAwait(false);
