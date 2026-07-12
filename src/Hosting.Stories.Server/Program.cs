Console.Title = "Norse Stories Server";
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
	app.UseWebAssemblyDebugging();
}

app.MapStaticAssets();

// BlazingStory derives the host app's scoped-CSS bundle name from the .csproj filename
// (Hosting.Stories.Client), not the Norse-branded AssemblyName the bundle actually ships
// under. Redirect the name it requests to the real branded asset.
app.MapGet("/Hosting.Stories.Client.styles.css", () => Results.Redirect("/Norse.Hosting.Stories.Client.styles.css"));

app.MapFallbackToFile("index.html");

await app
	.RunAsync()
	.ConfigureAwait(false);
