Console.Title = "Norse Migrations Service";
var builder = Host.CreateApplicationBuilder(args);
builder.AddNorseMigrations();
await builder.Build().RunAsync().ConfigureAwait(false);
