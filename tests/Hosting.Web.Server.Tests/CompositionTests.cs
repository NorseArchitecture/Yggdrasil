using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Norse.Abstractions.Contracts;
using Norse.Abstractions.Web.Server.Mediator;
using Norse.Infrastructure.ServiceDefaults.AspNet;
using ProtoBuf.Meta;

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
///     Wired-not-designed composition assertions (plan §Step 3): each boots the real <c>Program.cs</c>
///     composition root -- via <see cref="WebApplicationFactory{TEntryPoint}" />, never a hand-rolled
///     stand-in -- so every assertion here fails if the registration it checks is ever deleted from
///     <c>Program.cs</c>, not just if the underlying library API changes shape.
/// </summary>
public sealed class CompositionTests(WebApplicationFactory<Program> factory)
	: IClassFixture<WebApplicationFactory<Program>>
{
	[Fact]
	void Test_host_connection_strings_exist_before_factory_boot()
	{
		Environment.GetEnvironmentVariable("ConnectionStrings__norse_identity").ShouldNotBeNullOrWhiteSpace();
		Environment.GetEnvironmentVariable("ConnectionStrings__norse_reference").ShouldNotBeNullOrWhiteSpace();
	}

	[Fact]
	void AddNorsePipeline_registers_ISender_resolvable_in_a_scope()
	{
		using var scope = factory.Services.CreateScope();

		var sender = scope.ServiceProvider.GetRequiredService<ISender>();

		sender.ShouldNotBeNull();
	}

	[Fact]
	void AddNorseCodeFirstGrpc_registers_the_interceptor_stack_in_law_order()
	{
		using var scope = factory.Services.CreateScope();

		var options = scope.ServiceProvider.GetRequiredService<IOptions<GrpcServiceOptions>>().Value;

		// The three interceptor types are internal to Midgard's Infrastructure.Web.Server -- no
		// InternalsVisibleTo grant reaches this assembly, so they're compared by FullName rather than
		// typeof(), which needs no accessibility beyond reflection over the already-resolved Type.
		options.Interceptors.Select(registration => registration.Type.FullName).ShouldBe(
		[
			"Norse.Infrastructure.Web.Server.Mediator.Grpc.UnhandledExceptionInterceptor",
			"Norse.Infrastructure.Web.Server.Mediator.Grpc.PrincipalSeedingInterceptor",
			"Norse.Infrastructure.Web.Server.Mediator.Grpc.OutcomeServerInterceptor"
		]);
	}

	[Fact]
	void MapNorseGrpcServices_registers_an_outcome_surrogate_for_a_real_discovered_payload()
	{
		// The generated entry point is public and lives in this assembly's own namespace (the
		// generator emits into compilation.AssemblyName) -- called directly per the plan, independent
		// of whether a factory-booted host has already run app.MapNorseGrpcServices() itself.
		NorseGrpcServerRegistration.RegisterNorseOutcomeSurrogates();

		RuntimeTypeModel.Default.IsDefined(typeof(Outcome<NavigationResult>)).ShouldBeTrue();

		// protobuf-net 2.4.8's MetaType exposes SetSurrogate as a write-only fluent method -- there is
		// no public getter to read the configured surrogate back. GetSchema is the documented way to
		// observe it: once Outcome<NavigationResult> is surrogated to NavigationResult, the emitted .proto schema
		// describes NavigationResult's own shape (its DataMembers), not Outcome<T>'s private union layout.
		var schema = RuntimeTypeModel.Default.GetSchema(typeof(Outcome<NavigationResult>));
		schema.ShouldContain(nameof(NavigationResult.NextUrl));
	}

	[Fact]
	void CircuitHandler_registers_LoggingCircuitHandler()
	{
		using var scope = factory.Services.CreateScope();

		var circuitHandlers = scope.ServiceProvider.GetServices<CircuitHandler>();

		circuitHandlers.OfType<LoggingCircuitHandler>().ShouldNotBeEmpty();
	}

	// Liveness only -- MapDefaultEndpoints() maps /livez to just the live-tagged checks and /readyz to
	// every registered check (Midgard's own doc comment on the extension), and this fixture's fake,
	// unreachable ConnectionStrings__norse_identity/norse_reference (TestHostEnvironment's module
	// initializer) exist
	// precisely so nothing here opens a real connection. DbContextHealthCheck isn't live-tagged, so
	// /livez upholds that; /readyz would genuinely try to reach Postgres and hang on the timeout --
	// that's real infrastructure reachability, out of scope for a composition/wiring fixture.
	[Fact]
	async Task MapDefaultEndpoints_answers_the_liveness_probe_with_a_bare_healthy_body()
	{
		using var client = factory.CreateClient();

		var response = await client.GetAsync(new Uri(HealthEndpoints.Liveness, UriKind.Relative),
			TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
		body.ShouldBe("Healthy");
	}

	// The gRPC health service is polled by its own clients on a timer, exactly as aggressively as an
	// HTTP probe, so the DisableHttpMetrics() chained onto its map site is load-bearing rather than
	// decoration. Its traces are suppressed elsewhere -- AspNetTraceFilter knows the /grpc.health.
	// prefix -- which is why only the metrics half is assertable here.
	[Fact]
	void MapGrpcHealthChecksService_maps_the_health_endpoint_with_http_metrics_disabled()
	{
		var endpoints = factory.Services
			.GetRequiredService<EndpointDataSource>()
			.Endpoints
			.OfType<RouteEndpoint>()
			.Where(static endpoint =>
				endpoint.RoutePattern.RawText?.StartsWith("/grpc.health.", StringComparison.Ordinal) == true)
			.ToList();

		endpoints.ShouldNotBeEmpty();
		endpoints.ShouldAllBe(endpoint => endpoint.Metadata.GetMetadata<IDisableHttpMetricsMetadata>() != null);
	}

	[Fact]
	async Task The_live_OpenAPI_document_carries_only_the_reference_facade()
	{
		// Everything else the host maps is deliberately absent: identity's account endpoints and the
		// deferred sign-in door both carry ExcludeFromDescription() at their map sites, gRPC services
		// and health probes never produce ApiDescriptions, and no other realm ships a controller. If a
		// new path ever appears here, something started leaking into the public document -- rule on it,
		// don't widen the assertion reflexively.
		var document = await FetchOpenApiDocumentAsync();

		var paths = document["paths"]?.AsObject()
			?? throw new InvalidOperationException("document carries no paths");
		paths.Select(static path => path.Key).ShouldBe(["/api/reference/countries/{code}"]);
		paths["/api/reference/countries/{code}"]!.AsObject().Select(static op => op.Key).ShouldBe(["get"]);

		// The symmetry law holds on the real host's document exactly as on the swoop fixture's: the
		// underlying T renders, never the union.
		var schemas = document["components"]?["schemas"]?.AsObject()
			?? throw new InvalidOperationException("document carries no components.schemas");
		schemas.ContainsKey("CountryResponse").ShouldBeTrue();
		schemas.Select(static schema => schema.Key)
			.Any(static name => name.Contains("Result", StringComparison.Ordinal) ||
				name.Contains("Outcome", StringComparison.Ordinal))
			.ShouldBeFalse();
	}

	[Fact]
	async Task The_standard_response_codes_ride_the_facade_operation_in_declaration_order()
	{
		var document = await FetchOpenApiDocumentAsync();

		var operation = document["paths"]!["/api/reference/countries/{code}"]!["get"]!;
		var responses = operation["responses"]!.AsObject();

		// The action's own 200 leads; StandardResponsesTransformer appends the idiomatic balance in
		// its deliberate order -- {code} is a bound parameter, so both the 400 (untrusted input to
		// reject) and the 404 (a resource to miss) apply to this operation.
		responses.Select(static response => response.Key).ShouldBe(
			["200", "400", "401", "403", "404", "406", "429", "500", "502", "503", "504"]);

		// The 200 declares exactly the two channels the wire actually serves, JSON first -- never the
		// formatter-union noise (text/plain, text/json, text/xml) ApiExplorer produces unaided, which
		// sent Scalar's first-pick try-it request into an honest 406 for a media type the document
		// should never have promised.
		responses["200"]!["content"]!.AsObject().Select(static media => media.Key)
			.ShouldBe(["application/json", "application/xml"]);

		// The 400 negotiates both problem media types and references the Problem component the same
		// transformer registers -- the platform's actual failure body, not a description-only stub.
		var badRequestContent = responses["400"]!["content"]!.AsObject();
		badRequestContent.Select(static media => media.Key)
			.ShouldBe(["application/problem+json", "application/problem+xml"], ignoreOrder: true);
		badRequestContent["application/problem+json"]!["schema"]!["$ref"]!.GetValue<string>()
			.ShouldBe("#/components/schemas/Problem");
		document["components"]!["schemas"]!["Problem"]!["properties"]!["errors"].ShouldNotBeNull();
	}

	[Fact]
	async Task A_credentialless_call_to_the_facade_is_rejected_before_content_negotiation_runs()
	{
		// Was "...negotiates to 406 on the live host" -- true before Task 14 (principal-at-the-door,
		// Glitnir Platform/specs/2026-08-21-principal-at-the-door-design.md §2.6). CountriesController
		// derives from GrpcControllerBase, so it is machine-lane by inheritance (§5.1) and now rejected
		// at UseAuthorization() -- above MVC, above content negotiation -- for any call carrying no
		// bearer credential. Until Himinbjorg#49 lands the bearer scheme, nothing can satisfy that lane,
		// so this fact can no longer prove ReturnHttpNotAcceptable against the live facade; that proof
		// still exists on the swoop fixture's own host (Swoop/WiringTests.Probe_1a, which never gates on
		// this lane) -- currently red for an unrelated pre-existing reason (PrincipalAccessor.Seed
		// rejecting that fixture's GUID-less test principal), not a claim this comment makes about its
		// current pass/fail state. What this fact still proves, live: rejection happens above negotiation, honestly
		// (401, no body) -- LaneCompositionTests.The_reference_facade_is_closed_to_a_credentialless_caller
		// pins the same shape for the happy-path code, this one for a request that would otherwise 406.
		using var client = factory.CreateClient();
		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/reference/countries/banana");
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/csv"));

		using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Fact]
	async Task Scalar_serves_the_human_readable_reference_in_development()
	{
		// Same dev-only posture as gRPC reflection: the discovery surface exists at the bench, never in
		// the deployed footprint. WebApplicationFactory boots the host in Development, so the map takes.
		using var client = factory.CreateClient();

		var response = await client.GetAsync(new Uri("/scalar/v1", UriKind.Relative),
			TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
	}

	async Task<JsonNode> FetchOpenApiDocumentAsync()
	{
		using var client = factory.CreateClient();
		var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative),
			TestContext.Current.CancellationToken);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
		return JsonNode.Parse(json)!;
	}
}
