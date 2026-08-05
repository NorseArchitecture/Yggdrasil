using Norse.Infrastructure.Backend.Keys;
using Norse.Infrastructure.ServiceDefaults;

Console.Title = "Norse Migrations Service";
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddNorseMigrations();
// Dev-grade only, content-root-relative. This host's NorseIdentityDbContext registration is the
// migrations-only fallback (SchemaVersion forced to Version3, ProtectPersonalData left off), so the
// key seam is inert here today -- registered for parity with Hosting.Web.Server's DI graph and as a
// guard against a future path (design-time scaffolding, a contributor that does read protected
// columns) that would otherwise fail with no seam resolvable at all.
builder.Services.AddNorseDevelopmentKeys(Path.Combine(builder.Environment.ContentRootPath, "norse-dev-keys"));
await builder.Build().RunAsync().ConfigureAwait(false);
