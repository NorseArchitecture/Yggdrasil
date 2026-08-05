using Norse.Infrastructure.Backend.Keys;
using Norse.Infrastructure.ServiceDefaults;

Console.Title = "Norse Migrations Service";
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddNorseMigrations();
// Dev-grade only, content-root-relative: the Identity model builds with ProtectPersonalData=true,
// so this host's DI graph needs the same key seam as Hosting.Web.Server's, not just the schema.
builder.Services.AddNorseDevelopmentKeys(Path.Combine(builder.Environment.ContentRootPath, "norse-dev-keys"));
await builder.Build().RunAsync().ConfigureAwait(false);
