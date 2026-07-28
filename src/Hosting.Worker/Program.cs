using Norse.Infrastructure.ServiceDefaults;

Console.Title = "Norse Worker";
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
await builder.Build().RunAsync().ConfigureAwait(false);
