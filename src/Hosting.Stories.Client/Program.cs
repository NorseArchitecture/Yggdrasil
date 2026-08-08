using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Norse.DesignSystem.Stories;
using Norse.Hosting.Stories.Client;
using Norse.Infrastructure.Components.Theme.FluentUI;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services
	.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) })
	.AddNorseFluentUiTheme()
	.AddNorseStoryFakes();

await builder.Build().RunAsync().ConfigureAwait(false);
