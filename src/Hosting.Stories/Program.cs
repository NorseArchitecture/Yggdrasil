using BlazingStory.Components;
using BlazingStory.McpServer;
using Norse.DesignSystem.Stories;
using Norse.Hosting.Stories.Components.Pages;
using Norse.Infrastructure.Components.Theme.FluentUI;
using Norse.Infrastructure.ServiceDefaults.AspNet;

Console.Title = "Norse Stories";
var builder = WebApplication.CreateBuilder(args);
builder.AddAssetHostServiceDefaults();

builder.Services
	.AddRazorComponents()
	.AddInteractiveServerComponents();
builder.Services
	.AddNorseFluentUiTheme()
	.AddNorseStoryFakes()
	.AddBlazingStoryMcpServer();

var app = builder.Build();

app.MapStaticAssets().DisableHttpMetrics();
app.MapDefaultEndpoints();
app.MapBlazingStoryMcp();

app.UseAntiforgery();

app.MapRazorComponents<BlazingStoryServerComponent<IndexPage, IFramePage>>()
	.AddInteractiveServerRenderMode();

app.Run();
