using Norse.Infrastructure.ServiceDefaults;

Console.Title = "Norse Migrations Service";
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddNorseMigrations();
await builder.Build().RunAsync().ConfigureAwait(false);
