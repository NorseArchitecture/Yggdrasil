namespace Norse.Hosting.Worker.Tests;

public class PlaceholderTests
{
	[Fact]
	void ServiceName_returns_short_form() =>
		Placeholder.ServiceName().ShouldBe("Hosting.Worker");

	[Fact]
	void ServiceName_returns_qualified_form() =>
		Placeholder.ServiceName(qualified: true).ShouldBe("Norse.Hosting.Worker");
}
