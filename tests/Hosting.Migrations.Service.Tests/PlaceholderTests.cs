namespace Norse.Hosting.Migrations.Service.Tests;

public class PlaceholderTests
{
	[Fact]
	void ServiceName_returns_short_form() =>
		Placeholder.ServiceName().ShouldBe("Hosting.Migrations.Service");

	[Fact]
	void ServiceName_returns_qualified_form() =>
		Placeholder.ServiceName(qualified: true).ShouldBe("Norse.Hosting.Migrations.Service");
}
