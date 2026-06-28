Console.Title = "Norse Migrations Service";
var builder = Host.CreateApplicationBuilder(args);
await builder.Build().RunAsync().ConfigureAwait(false);
