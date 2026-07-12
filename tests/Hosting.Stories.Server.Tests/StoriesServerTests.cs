using Microsoft.AspNetCore.Mvc.Testing;

namespace Norse.Hosting.Stories.Server.Tests;

public class StoriesServerTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
	readonly HttpClient _client = factory.CreateClient();

	[Fact]
	async Task Root_serves_the_blazor_app_shell()
	{
		var response = await _client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

		response.EnsureSuccessStatusCode();
		var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
		body.ShouldContain("_framework/blazor.webassembly");
	}

	[Fact]
	async Task Deep_client_route_falls_back_to_the_app_shell()
	{
		var response = await _client.GetAsync(new Uri("/some/deep/client/route", UriKind.Relative), TestContext.Current.CancellationToken);

		response.EnsureSuccessStatusCode();
		var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
		body.ShouldContain("_framework/blazor.webassembly");
	}
}
