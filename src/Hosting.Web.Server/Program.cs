Console.Title = "Norse Web Server";
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
await app.RunAsync().ConfigureAwait(false);
