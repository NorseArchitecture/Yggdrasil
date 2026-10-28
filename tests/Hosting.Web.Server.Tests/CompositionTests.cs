using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentValidation;
using Grpc.AspNetCore.Server;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Norse.Abstractions.Contracts;
using Norse.Abstractions.Web.Server.Mediator;
using Norse.AuthN.Services;
using Norse.Identity.EntityFramework;
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

	// Train 2's account commands are internal to Himinbjorg (server-sovereign mediator identity), so they
	// are named by assembly-qualified string and closed over IValidator<> by reflection. Each must resolve
	// through the generated AddNorseIdentityWebServerHandlers() -- the CommandRequestValidator adapter
	// over Heimdall's wire validator -- never through a hand-written AddScoped here.
	[Theory]
	[InlineData("SetPhoneNumberCommand")]
	[InlineData("ChangePasswordCommand")]
	[InlineData("SetPasswordCommand")]
	[InlineData("ChangeEmailCommand")]
	[InlineData("RemoveExternalLoginCommand")]
	[InlineData("DeletePersonalDataCommand")]
	void The_account_command_validators_resolve_through_generated_discovery(string command)
	{
		var commandType = Type.GetType($"Norse.Identity.Web.Server.Account.{command}, Norse.Identity.Web.Server", throwOnError: true)!;
		using var scope = factory.Services.CreateScope();

		var validator = scope.ServiceProvider.GetService(typeof(IValidator<>).MakeGenericType(commandType));

		validator.ShouldNotBeNull();
		validator.GetType().GetGenericTypeDefinition().ShouldBe(typeof(CommandRequestValidator<,,>));
	}

	// Defect A of the Train 2 walk (Ruling 41): Midgard's two policy schemes once carried their own names
	// as display names, and SignInManager offers every scheme with a display name as an external login --
	// so the Manage page listed "Norse" and "Norse.Machine". With no external provider configured here,
	// the composed host must offer none at all.
	[Fact]
	async Task The_composed_host_offers_no_external_login_scheme_when_no_provider_is_configured()
	{
		using var scope = factory.Services.CreateScope();
		var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<NorseUser>>();

		var schemes = await signInManager.GetExternalAuthenticationSchemesAsync();

		schemes.ShouldBeEmpty();
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
		// bearer credential. Himinbjorg#49's bearer scheme has since landed (Norse.Machine, forwarding to
		// OpenIddict's validation handler -- AuthenticationBuilderExtensions.AddNorseAuthentication), but
		// this WebApplicationFactory host has no real database and no seeded machine client to mint a
		// token against, so no credential this fact could present would ever satisfy that lane -- this
		// fact still can't prove ReturnHttpNotAcceptable against the live facade; that proof still exists
		// on the swoop fixture's own host (Swoop/WiringTests.Probe_1a, which never gates on this lane).
		// The real end-to-end proof, with a seeded client and a genuine token, is
		// MachineAuthE2ETests (needs Docker/Testcontainers Postgres). What this fact still proves, live:
		// rejection happens above negotiation, honestly (401, no body) --
		// LaneCompositionTests.The_reference_facade_is_closed_to_a_credentialless_caller pins the same
		// shape for the happy-path code, this one for a request that would otherwise 406.
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

	// Train 3 (../Glitnir/docs/Himinbjorg/plans/2026-10-27-identity-account-lifecycle-train-3.md, Task 6.1): every
	// two-factor and passkey page left Himinbjorg's scaffold for Heimdall. A stale Himinbjorg copy on the same
	// template would throw on duplicate routes at startup; the routed component's own assembly is the proof the
	// survivor is Heimdall's.
	[Theory]
	[InlineData("/Account/Manage/TwoFactorAuthentication")]
	[InlineData("/Account/Manage/EnableAuthenticator")]
	[InlineData("/Account/Manage/ShowRecoveryCodes")]
	[InlineData("/Account/Manage/ResetAuthenticator")]
	[InlineData("/Account/Manage/Disable2fa")]
	[InlineData("/Account/Manage/GenerateRecoveryCodes")]
	[InlineData("/Account/Manage/Passkeys")]
	[InlineData("/Account/Manage/RenamePasskey/{Id}")]
	[InlineData("/Account/LoginWithPasskey")]
	void The_train_3_pages_route_from_heimdalls_assembly(string route)
	{
		var endpoints = EndpointsAt(route);

		endpoints.ShouldNotBeEmpty();
		endpoints.ShouldAllBe(static endpoint =>
			endpoint.Metadata.GetMetadata<ComponentTypeMetadata>() != null &&
			endpoint.Metadata.GetMetadata<ComponentTypeMetadata>()!.Type.Assembly.GetName().Name == "Norse.AuthN.Components.FluentUI");
	}

	// Heimdall's passkey ceremonies import their own module; Himinbjorg's PasskeySubmit.razor.js is gone, and with it
	// the App.razor script tag that loaded it.
	[Fact]
	async Task The_passkeys_module_is_served_as_a_static_web_asset()
	{
		using var client = factory.CreateClient();

		var response = await client.GetAsync(new Uri("/_content/Norse.AuthN.Components.FluentUI/passkeys.js", UriKind.Relative),
			TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		response.Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");
	}

	// The NavMenu's logout is a link to Heimdall's click-to-confirm page (A62); the page's own POST is the
	// component endpoint, never the minimal-API delegate Himinbjorg's retired MapAdditionalIdentityEndpoints mapped.
	[Fact]
	void The_logout_post_is_heimdalls_component_endpoint()
	{
		var endpoints = EndpointsAt("/Account/Logout");

		endpoints.Where(static endpoint => AcceptsPost(endpoint)).ShouldNotBeEmpty();
		endpoints.ShouldAllBe(static endpoint =>
			endpoint.Metadata.GetMetadata<ComponentTypeMetadata>() != null &&
			endpoint.Metadata.GetMetadata<ComponentTypeMetadata>()!.Type.Assembly.GetName().Name == "Norse.AuthN.Components");
	}

	// Passkey ceremonies ride gRPC (IPasskeyService, ISignInMethodsService): the scaffold's POST option endpoints
	// (/Account/PasskeyCreationOptions, /Account/PasskeyRequestOptions) are gone. The /Account tree is the surface they
	// lived on, and its component endpoints accept a form POST by construction, so only its other endpoints are judged.
	[Fact]
	void No_passkey_post_endpoint_remains()
	{
		var endpoints = factory.Services
			.GetRequiredService<EndpointDataSource>()
			.Endpoints
			.OfType<RouteEndpoint>()
			.Where(static endpoint => endpoint.Metadata.GetMetadata<ComponentTypeMetadata>() is null)
			.Where(static endpoint =>
				endpoint.RoutePattern.RawText?.StartsWith("/Account/", StringComparison.OrdinalIgnoreCase) == true
				&& endpoint.RoutePattern.RawText.Contains("passkey", StringComparison.OrdinalIgnoreCase));

		endpoints.Where(static endpoint => AcceptsPost(endpoint))
			.Select(static endpoint => endpoint.RoutePattern.RawText)
			.ShouldBeEmpty();
	}

	// T3-34: the external sign-in start is a server-authored link (GetSignInMethods' StartUrl), so it is a GET that
	// fails closed on Fetch Metadata, never an antiforgery-guarded POST.
	[Fact]
	void The_external_start_is_a_get_endpoint()
	{
		var endpoint = EndpointsAt("/Account/PerformExternalLogin").ShouldHaveSingleItem();

		endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods.ShouldBe(["GET"]);
	}

	// Components inject the contracts directly. On the server render they resolve to Himinbjorg's registered services
	// (AddNorseAuthenticationService); on the WASM render to the generated gRPC proxies. Both halves are pinned here.
	[Fact]
	void The_two_factor_passkey_and_sign_in_methods_contracts_resolve_on_the_client()
	{
		using var scope = factory.Services.CreateScope();

		scope.ServiceProvider.GetRequiredService<ITwoFactorService>().GetType().FullName
			.ShouldBe("Norse.Identity.Web.Server.TwoFactor.TwoFactorService");
		scope.ServiceProvider.GetRequiredService<IPasskeyService>().GetType().FullName
			.ShouldBe("Norse.Identity.Web.Server.Passkeys.PasskeyService");
		scope.ServiceProvider.GetRequiredService<ISignInMethodsService>().GetType().FullName
			.ShouldBe("Norse.Identity.Web.Server.SignInMethods.SignInMethodsService");

		ServiceCollection clientServices = new();
		using var channel = GrpcChannel.ForAddress("http://localhost");
		Client.NorseGrpcClientRegistration.AddNorseGrpcClients(clientServices, channel);
		using var client = clientServices.BuildServiceProvider();
		client.GetService<ITwoFactorService>().ShouldNotBeNull();
		client.GetService<IPasskeyService>().ShouldNotBeNull();
		client.GetService<ISignInMethodsService>().ShouldNotBeNull();
	}

	// T3-27: the issuer is required configuration with no code default. Development supplies it
	// (appsettings.Development.json); Production supplies nothing here, so the host fails before it serves.
	[Fact]
	void A_missing_authenticator_issuer_fails_startup()
	{
		using var production = factory.WithWebHostBuilder(static builder => builder.UseEnvironment("Production"));

		var exception = Should.Throw<OptionsValidationException>(() => production.Services);

		exception.Failures.ShouldContain(static failure => failure.Contains("Issuer", StringComparison.Ordinal));
	}

	List<RouteEndpoint> EndpointsAt(string route) =>
	[
		.. factory.Services
			.GetRequiredService<EndpointDataSource>()
			.Endpoints
			.OfType<RouteEndpoint>()
			.Where(endpoint => endpoint.RoutePattern.RawText == route)
	];

	// No method metadata means the endpoint answers every method, POST included.
	static bool AcceptsPost(Endpoint endpoint) =>
		endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") != false;

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
