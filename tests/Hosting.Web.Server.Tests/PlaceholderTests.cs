namespace Norse.Hosting.Web.Server.Tests;

public class PlaceholderTests
{
	[Fact]
	void ServiceName_returns_short_form() =>
		Placeholder.ServiceName().ShouldBe("Hosting.Web.Server");

	[Fact]
	void ServiceName_returns_qualified_form() =>
		Placeholder.ServiceName(qualified: true).ShouldBe("Norse.Hosting.Web.Server");
}
