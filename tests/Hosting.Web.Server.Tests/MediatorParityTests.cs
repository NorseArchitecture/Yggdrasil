using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentValidation;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Norse.Abstractions.Contracts;
using Norse.Abstractions.Web.Server.Mediator;
using Norse.AuthN.Components;
using Norse.AuthN.Services;
using Norse.Infrastructure.Web.Client.Grpc;
using Norse.Infrastructure.Web.Server.Authentication;
using Norse.Infrastructure.Web.Server.Mediator;
using Norse.Infrastructure.Web.Server.Mediator.Grpc;
using Norse.Primitives;
using ProtoBuf.Grpc.Client;

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
///     The server-sovereign mediator identity a real gRPC service (<see cref="TestAuthenticationService" />)
///     hydrates <see cref="LoginRequest" /> into and sends -- the test-local mirror of Himinbjörg's real
///     <c>LoginCommand</c>, carrying the same policy so <c>AuthorizationBehavior</c> and the registration
///     generator's NORSE011 backstop (<c>PolicyCache&lt;TRequest&gt;</c>) both see a request that plays by
///     platform law.
/// </summary>
[Authorize(Policy = AuthNPolicies.Public)]
sealed record TestLoginCommand(LoginRequest Request) : CommandRequest<LoginRequest, NavigationResult>(Request);

/// <summary>
///     A handler whose behavior each test controls via the injected delegate -- swapped per test instead
///     of per assertion, so each test gets its own host with its own stub wired in from the start.
/// </summary>
sealed class StubLoginHandler(Func<LoginRequest, CancellationToken, ValueTask<Outcome<NavigationResult>>> handle) :
	IRequestHandler<TestLoginCommand, NavigationResult>
{
	public ValueTask<Outcome<NavigationResult>> Handle(TestLoginCommand request,
		CancellationToken cancellationToken = default) =>
		handle(request.Request, cancellationToken);
}

/// <summary>
///     The wire (gRPC) leg's own real, test-only implementation of the machine lane
///     (<see cref="NorseSchemes.Machine" />) -- mints a genuine GUID-bearing principal so
///     <c>PrincipalAccessor.Seed</c>'s GUID backstop (Midgard, already shipped) sees real credentials when
///     <see cref="PrincipalSeedingInterceptor" /> stamps it from the authenticated
///     <c>HttpContext.User</c>, rather than the default anonymous, claim-less principal this suite's wire
///     leg used to reach that call with implicitly. <see cref="AuthNPolicies.Public" />'s permissive
///     assertion still means every command in this suite passes authorization regardless of who this
///     principal is; only its existence (and its GUID claim) matters.
/// </summary>
sealed class TestMachinePrincipalHandler(
	IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
	ILoggerFactory logger,
	UrlEncoder encoder)
	: Microsoft.AspNetCore.Authentication.AuthenticationHandler<
		Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(options, logger, encoder)
{
	// Microsoft.AspNetCore.Authentication is not `using`d in this file -- Norse.AuthN.Services.IAuthenticationService
	// (the platform's own mediator-facing contract, used throughout this file) would otherwise collide
	// with Microsoft.AspNetCore.Authentication.IAuthenticationService (CS0104), so the few ASP.NET Core
	// authentication types this handler needs are fully qualified instead.
	protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
	{
		ClaimsIdentity identity = new(
			[new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
			authenticationType: NorseSchemes.Machine);
		Microsoft.AspNetCore.Authentication.AuthenticationTicket ticket =
			new(new ClaimsPrincipal(identity), NorseSchemes.Machine);
		return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(ticket));
	}
}

/// <summary>
///     The circuit path's own <see cref="IPrincipalAccessor" /> (spec §2.6): overrides
///     <c>AddNorsePipeline()</c>'s own registration (DI resolves the last registration for a
///     single-service ask) so a bare <c>host.Services.CreateScope()</c> call -- no HTTP request, no
///     <see cref="Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider" /> -- still
///     has a genuine GUID-bearing principal to hand <c>AuthorizationBehavior</c>, mirroring what a real
///     Blazor circuit's <c>AuthenticationStateProvider</c> would supply. Unrelated to the wire leg's own
///     fix above: <see cref="PrincipalSeedingInterceptor" /> seeds the concrete <c>PrincipalAccessor</c>
///     type directly from <c>HttpContext.User</c>, never this interface override, so the wire leg's
///     correctness rides entirely on <see cref="TestMachinePrincipalHandler" /> above.
/// </summary>
sealed class TestCircuitPrincipalAccessor(ClaimsPrincipal principal) : IPrincipalAccessor
{
	public ValueTask<ClaimsPrincipal> GetPrincipalAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(principal);
}

/// <summary>
///     The test-local stand-in for Himinbjörg's real <c>AuthenticationService</c> -- same hydrate-and-send
///     shape (<see cref="Login" /> wraps <see cref="LoginRequest" /> in <see cref="TestLoginCommand" /> and
///     sends it through the real pipeline), minus the EF/Identity backend. <see cref="Logout" /> returns
///     directly rather than through the mediator: its whole subject is the wire mechanics of a
///     <see cref="CancellationToken" />-only operation, not another pipeline round trip already proven by
///     the other three tests.
/// </summary>
sealed class TestAuthenticationService(ISender sender) : IAuthenticationService
{
	public Task<Outcome<NavigationResult>> Login(LoginRequest request, CancellationToken cancellationToken = default) =>
		sender.Send(new TestLoginCommand(request), cancellationToken).AsTask();

	public Task<Outcome<NavigationResult>> Register(RegisterRequest request,
		CancellationToken cancellationToken = default) =>
		throw new NotSupportedException($"{nameof(Register)} is not exercised by {nameof(MediatorParityTests)}.");

	public Task<Outcome<BoolResponse>> EmailExists(EmailExistsRequest request,
		CancellationToken cancellationToken = default) =>
		throw new NotSupportedException($"{nameof(EmailExists)} is not exercised by {nameof(MediatorParityTests)}.");

	public Task<Outcome<NavigationResult>> Logout(CancellationToken cancellationToken = default) =>
		Task.FromResult(Outcome<NavigationResult>.Ok(new NavigationResult { NextUrl = "/" }));
}

/// <summary>
///     Acceptance proof for the mediator pipeline that replaced the code-generated gateway (spec §5): one
///     self-contained <see cref="TestServer" /> host, hand-registered exactly as the registration generator
///     would emit it for a real handled request, proving the circuit path (direct <see cref="ISender" />)
///     and the wire path (a real gRPC call) render the same <see cref="Outcome{T}" /> -- including the wire
///     path's server-side validation and exception-to-fault translation, neither of which the retired
///     gateway design could ever exercise end to end.
/// </summary>
public sealed class MediatorParityTests
{
	static async Task<IHost> CreateHost(
		Func<LoginRequest, CancellationToken, ValueTask<Outcome<NavigationResult>>> handleLogin,
		CancellationToken cancellationToken)
	{
		// Real deployments reach this through MapNorseGrpcServices(), which calls it before mapping --
		// this host maps TestAuthenticationService directly (the generator only ever saw Hosting.Web.
		// Server's own compilation, never this test-local service). No need to register the payload
		// surrogates here: WireModelFixture (an [assembly: AssemblyFixture]) already did it, guaranteed
		// complete before any test in this assembly runs.

		// The circuit path below (a bare ISender.Send from host.Services.CreateScope(), no HTTP request
		// at all) has nothing for PrincipalSeedingInterceptor to seed and no AuthenticationStateProvider
		// (no Blazor circuit exists in this host) -- Midgard's concrete PrincipalAccessor throws loudly
		// when neither is present, by design. A genuine GUID-bearing principal, supplied directly here,
		// models what a real circuit's AuthenticationStateProvider would hand back.
		var circuitPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
			[new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], authenticationType: "Test.Circuit"));

		return await new HostBuilder()
			.ConfigureWebHost(webHost =>
			{
				webHost.UseTestServer();
				webHost.ConfigureServices(services =>
				{
					services.AddLogging();
					services.AddAuthorizationBuilder()
						.AddPolicy(AuthNPolicies.Public, p => p.RequireAssertion(_ => true));
					// The wire (gRPC) leg's real machine-lane credentials (spec §2.6): a genuine
					// test-only authentication scheme forwarded to NorseSchemes.Machine, so
					// PrincipalSeedingInterceptor stamps a real GUID-bearing HttpContext.User instead of
					// the default anonymous, claim-less principal PrincipalAccessor.Seed's GUID backstop
					// (Midgard, already shipped) rejects.
					services.AddAuthentication(NorseSchemes.Machine)
						.AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
							TestMachinePrincipalHandler>(NorseSchemes.Machine, null);
					services.AddNorsePipeline();
					services.AddNorseCodeFirstGrpc();
					services.AddRouting();

					// Mirrors exactly what Asgard's registration generator emits for a real handled
					// request -- generated-emission correctness is already proven in Asgard/Himinbjörg's
					// own suites; this proves the runtime matrix, hand-wired the same way.
					services.AddScoped<IRequestHandler<TestLoginCommand, NavigationResult>>(_ =>
						new StubLoginHandler(handleLogin));
					services.AddSingleton<ISenderDispatch, SenderDispatch<TestLoginCommand, NavigationResult>>();
					services.AddScoped<IValidator<LoginRequest>, LoginRequestValidator>();
					services
						.AddScoped<IValidator<TestLoginCommand>,
							CommandRequestValidator<TestLoginCommand, LoginRequest, NavigationResult>>();

					services.AddScoped<IAuthenticationService, TestAuthenticationService>();
					services.AddScoped<IPrincipalAccessor>(_ => new TestCircuitPrincipalAccessor(circuitPrincipal));
				});
				webHost.Configure(app =>
				{
					app.UseRouting();
					app.UseAuthentication();
					app.UseAuthorization();
					app.UseEndpoints(endpoints => endpoints.MapGrpcService<TestAuthenticationService>());
				});
			})
			.StartAsync(cancellationToken);
	}

	static IAuthenticationService CreateWireClient(IHost host)
	{
		// TestServer's in-memory handler has no TLS to negotiate -- real deployments always terminate
		// TLS in front of the gRPC endpoint, but Grpc.Net.Client refuses even to attempt an HTTP/2 call
		// over a plain "http://" address without this opt-in (mirrors WirePathAuthorizationTests).
		AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

		var channel = GrpcChannel.ForAddress("http://localhost",
			new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
		var invoker = channel.Intercept(new OutcomeClientInterceptor());
		return GrpcClientFactory.CreateGrpcService<IAuthenticationService>(invoker);
	}

	static LoginRequest ValidLoginRequest() =>
		new() { EmailInput = "user@example.com", Password = "Password1", RememberMe = false };

	[Fact]
	async Task LockedOut_renders_identically_through_the_circuit_path_and_the_wire_path()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var host = await CreateHost(
			(_, _) => ValueTask.FromResult(Outcome<NavigationResult>.Err(ErrorCategory.LockedOut,
				errors: new Dictionary<string, string[]> { [""] = ["locked"] })),
			cancellationToken);

		using var scope = host.Services.CreateScope();
		var circuitOutcome = await scope.ServiceProvider.GetRequiredService<ISender>()
			.Send(new TestLoginCommand(ValidLoginRequest()), cancellationToken);

		var wireOutcome = await CreateWireClient(host).Login(ValidLoginRequest(), cancellationToken);

		circuitOutcome.TryGetValue(out Failed circuitFailed).ShouldBeTrue();
		circuitFailed.Problem.Category.ShouldBe(ErrorCategory.LockedOut);
		circuitFailed.Problem.Errors[""].ShouldBe(["locked"]);

		wireOutcome.TryGetValue(out Failed wireFailed).ShouldBeTrue();
		wireFailed.Problem.Category.ShouldBe(ErrorCategory.LockedOut);
		wireFailed.Problem.Errors[""].ShouldBe(["locked"]);
	}

	[Fact]
	async Task A_handler_throw_reaches_the_wire_client_as_Fault_with_a_correlation_id()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var host = await CreateHost(
			(_, _) => throw new InvalidOperationException("boom"),
			cancellationToken);

		var outcome = await CreateWireClient(host).Login(ValidLoginRequest(), cancellationToken);

		outcome.TryGetValue(out Failed failed).ShouldBeTrue();
		failed.Problem.Category.ShouldBe(ErrorCategory.Fault);
		failed.Problem.CorrelationId.ShouldNotBeNull();
	}

	[Fact]
	async Task Parameterless_logout_crosses_the_wire()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var host = await CreateHost(
			(_, _) => throw new InvalidOperationException("Login is not exercised by this test."),
			cancellationToken);

		var outcome = await CreateWireClient(host).Logout(cancellationToken);

		outcome.TryGetValue(out Success<NavigationResult> success).ShouldBeTrue();
		success.Value.ShouldBeOfType<NavigationResult>();
	}
}
