using Microsoft.AspNetCore.Mvc.Testing;
using Norse.Infrastructure.ServiceDefaults.AspNet;

namespace Norse.Hosting.Stories.Tests;

public sealed class StoriesServerTests(WebApplicationFactory<Program> factory)
	: IClassFixture<WebApplicationFactory<Program>>
{
	readonly HttpClient _client = factory.CreateClient();

	[Fact]
	async Task Root_serves_the_blazor_app_shell()
	{
		var response = await _client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

		response.EnsureSuccessStatusCode();
		var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
		// Confirmed live (Task 6, 2026-08-22): under Interactive Server this marker comment is
		// emitted by Blazor Web's own component-serialization boundary and only appears when the
		// root component actually rendered as a Server render-mode boundary -- a static or
		// misrouted fallback page never carries it. It replaces the WASM-era
		// "_framework/blazor.webassembly" substring check, which no longer holds: this host serves
		// no .wasm runtime or blazor.webassembly.*.js asset at all.
		body.ShouldContain("<!--Blazor-Server-Component-State:");
	}

	[Fact]
	async Task Deep_client_route_falls_back_to_the_app_shell()
	{
		var response = await _client.GetAsync(new Uri("/some/deep/client/route", UriKind.Relative),
			TestContext.Current.CancellationToken);

		response.EnsureSuccessStatusCode();
		var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
		body.ShouldContain("<!--Blazor-Server-Component-State:");
	}

	// The asset host's probes are the one place a body assertion is load-bearing rather than
	// cosmetic: this host also serves an index.html fallback, so a probe path that never got mapped
	// still answers 200 -- with the app shell. Only the body distinguishes a real probe from the
	// fallback swallowing it. The asset root (AddAssetHostServiceDefaults) registers the Norse.Probe
	// policy, and WebApplication adds the authorization middleware once it exists: before that, both
	// probes answered 500 ("The AuthorizationPolicy named: 'Norse.Probe' was not found").
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
