using System.Net;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Norse.Abstractions.Contracts;
using Norse.Abstractions.Web.Server.Mediator;
using Norse.AuthN.Services;
using Norse.Infrastructure.ServiceDefaults.AspNet;
using ProtoBuf.Meta;

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
/// Wired-not-designed composition assertions (plan §Step 3): each boots the real <c>Program.cs</c>
/// composition root -- via <see cref="WebApplicationFactory{TEntryPoint}"/>, never a hand-rolled
/// stand-in -- so every assertion here fails if the registration it checks is ever deleted from
/// <c>Program.cs</c>, not just if the underlying library API changes shape.
/// </summary>
public sealed class CompositionTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
	// Program.cs reads builder.Configuration.GetConnectionString(...) synchronously, before
	// builder.Build() runs -- earlier than WebApplicationFactory's WithWebHostBuilder/
	// ConfigureAppConfiguration hooks apply (those decorate the deferred host builder, which only
	// takes effect at Build()). WebApplicationBuilder.CreateBuilder(args) does include environment
	// variables among its default sources from the very start, so a process env var set before the
	// factory's first host boot is the one override Program.cs's own pre-Build() read can see.
	// The real norse_identity connection string comes from Aspire at runtime; this only needs to be
	// syntactically valid Npgsql for AddNorseAuthenticationService's DI-time registration -- nothing
	// exercised by these tests ever opens a connection.
	static CompositionTests() =>
		Environment.SetEnvironmentVariable("ConnectionStrings__norse_identity", "Host=localhost;Database=norse_identity_composition_tests;Username=test;Password=test");

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
			"Norse.Infrastructure.Web.Server.Mediator.Grpc.OutcomeServerInterceptor",
		]);
	}

	[Fact]
	void MapNorseGrpcServices_registers_an_outcome_surrogate_for_a_real_discovered_payload()
	{
		// The generated entry point is public and lives in this assembly's own namespace (the
		// generator emits into compilation.AssemblyName) -- called directly per the plan, independent
		// of whether a factory-booted host has already run app.MapNorseGrpcServices() itself.
		NorseGrpcServerRegistration.RegisterNorseOutcomeSurrogates();

		RuntimeTypeModel.Default.IsDefined(typeof(Outcome<LoginResult>)).ShouldBeTrue();

		// protobuf-net 2.4.8's MetaType exposes SetSurrogate as a write-only fluent method -- there is
		// no public getter to read the configured surrogate back. GetSchema is the documented way to
		// observe it: once Outcome<LoginResult> is surrogated to LoginResult, the emitted .proto schema
		// describes LoginResult's own shape (its DataMembers), not Outcome<T>'s private union layout.
		var schema = RuntimeTypeModel.Default.GetSchema(typeof(Outcome<LoginResult>));
		schema.ShouldContain(nameof(LoginResult.Succeeded));
	}

	[Fact]
	void CircuitHandler_registers_LoggingCircuitHandler()
	{
		using var scope = factory.Services.CreateScope();

		var circuitHandlers = scope.ServiceProvider.GetServices<CircuitHandler>();

		circuitHandlers.OfType<LoggingCircuitHandler>().ShouldNotBeEmpty();
	}

	[Theory]
	[InlineData(HealthEndpoints.Liveness)]
	[InlineData(HealthEndpoints.Readiness)]
	async Task MapDefaultEndpoints_answers_the_probe_with_a_bare_healthy_body(string path)
	{
		using var client = factory.CreateClient();

		var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

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
			.Where(static endpoint => endpoint.RoutePattern.RawText?.StartsWith("/grpc.health.", StringComparison.Ordinal) == true)
			.ToList();

		endpoints.ShouldNotBeEmpty();
		endpoints.ShouldAllBe(endpoint => endpoint.Metadata.GetMetadata<IDisableHttpMetricsMetadata>() != null);
	}
}
