using Microsoft.AspNetCore.Mvc.Testing;
using Norse.Infrastructure.ServiceDefaults.AspNet;

namespace Norse.Hosting.Stories.Server.Tests;

public sealed class StoriesServerTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
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

	// The asset host's probes are the one place a body assertion is load-bearing rather than
	// cosmetic: this host also serves an index.html fallback, so a probe path that never got mapped
	// still answers 200 -- with the app shell. Only the body distinguishes a real probe from the
	// fallback swallowing it.
	[Theory]
	[InlineData(HealthEndpoints.Liveness)]
	[InlineData(HealthEndpoints.Readiness)]
	async Task Probe_reports_healthy_rather_than_the_app_shell(string path)
	{
		var response = await _client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

		response.EnsureSuccessStatusCode();
		var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
		body.ShouldBe("Healthy");
	}
}
