using Microsoft.Extensions.Hosting;

namespace Norse.Hosting.Migrations.Service.Tests;

public class NorseMigrationsGeneratedExtensionsTests
{
	[Fact]
	void AddNorseMigrations_registers_all_contributors_without_throwing()
	{
		var builder = Host.CreateApplicationBuilder();
		builder.Configuration["ConnectionStrings:norse_identity"] = "Host=localhost;Database=test";
		builder.Configuration["ConnectionStrings:norse_reference"] = "Host=localhost;Database=test";

		Should.NotThrow(() => builder.AddNorseMigrations());
	}
}
