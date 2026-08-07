using System.Security.Claims;
using FluentValidation;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Norse.Abstractions.Contracts;
using Norse.Abstractions.Web.Server.Mediator;
using Norse.AuthN.Components;
using Norse.AuthN.Services;
using Norse.Infrastructure.Web.Client.Grpc;
using Norse.Infrastructure.Web.Server.Mediator;
using Norse.Infrastructure.Web.Server.Mediator.Grpc;
using Norse.Primitives;
using ProtoBuf.Grpc.Client;

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
/// The server-sovereign mediator identity a real gRPC service (<see cref="TestAuthenticationService"/>)
/// hydrates <see cref="LoginRequest"/> into and sends -- the test-local mirror of Himinbjörg's real
/// <c>LoginCommand</c>, carrying the same policy so <c>AuthorizationBehavior</c> and the registration
/// generator's NORSE011 backstop (<c>PolicyCache&lt;TRequest&gt;</c>) both see a request that plays by
/// platform law.
/// </summary>
[Authorize(Policy = AuthNPolicies.Public)]
sealed record TestLoginCommand(LoginRequest Request) : CommandRequest<LoginRequest, LoginResult>(Request);

/// <summary>
/// A handler whose behavior each test controls via the injected delegate -- swapped per test instead
/// of per assertion, so each test gets its own host with its own stub wired in from the start.
/// </summary>
sealed class StubLoginHandler(Func<LoginRequest, CancellationToken, ValueTask<Outcome<LoginResult>>> handle) :
	IRequestHandler<TestLoginCommand, LoginResult>
{
	public ValueTask<Outcome<LoginResult>> Handle(TestLoginCommand request, CancellationToken cancellationToken = default) =>
		handle(request.Request, cancellationToken);
}

/// <summary>
/// Overrides <c>AddNorsePipeline()</c>'s own <see cref="IPrincipalAccessor"/> registration (DI resolves
/// the last registration for a single-service ask, so registering this after <c>AddNorsePipeline()</c>
/// wins) so <c>AuthorizationBehavior</c> always has a principal to ask about, on both the circuit and
/// wire paths alike -- Midgard's concrete <c>PrincipalAccessor</c> throws when unseeded and no
/// <see cref="Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider"/> is
/// registered, and this test suite has neither a circuit nor a cookie scheme to seed one for real.
/// <see cref="AuthNPolicies.Public"/>'s permissive assertion means every command in this suite passes
/// authorization regardless of who this principal is -- its only job is existing.
/// </summary>
sealed class TestPrincipalAccessor(ClaimsPrincipal principal) : IPrincipalAccessor
{
	public ValueTask<ClaimsPrincipal> GetPrincipalAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(principal);
}

/// <summary>
/// The test-local stand-in for Himinbjörg's real <c>AuthenticationService</c> -- same hydrate-and-send
/// shape (<see cref="Login"/> wraps <see cref="LoginRequest"/> in <see cref="TestLoginCommand"/> and
/// sends it through the real pipeline), minus the EF/Identity backend. <see cref="Logout"/> returns
/// directly rather than through the mediator: its whole subject is the wire mechanics of a
/// <see cref="CancellationToken"/>-only operation, not another pipeline round trip already proven by
/// the other three tests.
/// </summary>
sealed class TestAuthenticationService(ISender sender) : IAuthenticationService
{
	public Task<Outcome<LoginResult>> Login(LoginRequest request, CancellationToken cancellationToken = default) =>
		sender.Send(new TestLoginCommand(request), cancellationToken).AsTask();

	public Task<Outcome<RegisterResult>> Register(RegisterRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException($"{nameof(Register)} is not exercised by {nameof(MediatorParityTests)}.");

	public Task<Outcome<BoolResponse>> EmailExists(EmailExistsRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException($"{nameof(EmailExists)} is not exercised by {nameof(MediatorParityTests)}.");

	public Task<Outcome<LogoutResult>> Logout(CancellationToken cancellationToken = default) =>
		Task.FromResult(Outcome<LogoutResult>.Ok(new LogoutResult()));
}

/// <summary>
/// Acceptance proof for the mediator pipeline that replaced the code-generated gateway (spec §5): one
/// self-contained <see cref="TestServer"/> host, hand-registered exactly as the registration generator
/// would emit it for a real handled request, proving the circuit path (direct <see cref="ISender"/>)
/// and the wire path (a real gRPC call) render the same <see cref="Outcome{T}"/> -- including the wire
/// path's server-side validation and exception-to-fault translation, neither of which the retired
/// gateway design could ever exercise end to end.
/// </summary>
public sealed class MediatorParityTests
{
	static async Task<IHost> CreateHost(
		Func<LoginRequest, CancellationToken, ValueTask<Outcome<LoginResult>>> handleLogin, CancellationToken cancellationToken)
	{
		// Real deployments reach this through MapNorseGrpcServices(), which calls it before mapping --
		// this host maps TestAuthenticationService directly (the generator only ever saw Hosting.Web.
		// Server's own compilation, never this test-local service). No need to register the payload
		// surrogates here: WireModelFixture (an [assembly: AssemblyFixture]) already did it, guaranteed
		// complete before any test in this assembly runs.

		var principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test"));

		return await new HostBuilder()
			.ConfigureWebHost(webHost =>
			{
				webHost.UseTestServer();
				webHost.ConfigureServices(services =>
				{
					services.AddLogging();
					services.AddAuthorizationBuilder().AddPolicy(AuthNPolicies.Public, p => p.RequireAssertion(_ => true));
					services.AddNorsePipeline();
					services.AddNorseCodeFirstGrpc();
					services.AddRouting();

					// Mirrors exactly what Asgard's registration generator emits for a real handled
					// request -- generated-emission correctness is already proven in Asgard/Himinbjörg's
					// own suites; this proves the runtime matrix, hand-wired the same way.
					services.AddScoped<IRequestHandler<TestLoginCommand, LoginResult>>(_ => new StubLoginHandler(handleLogin));
					services.AddSingleton<ISenderDispatch, SenderDispatch<TestLoginCommand, LoginResult>>();
					services.AddScoped<IValidator<LoginRequest>, LoginRequestValidator>();
					services.AddScoped<IValidator<TestLoginCommand>, CommandRequestValidator<TestLoginCommand, LoginRequest, LoginResult>>();

					services.AddScoped<IAuthenticationService, TestAuthenticationService>();
					services.AddScoped<IPrincipalAccessor>(_ => new TestPrincipalAccessor(principal));
				});
				webHost.Configure(app =>
				{
					app.UseRouting();
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

		var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
		var invoker = channel.Intercept(new OutcomeClientInterceptor());
		return GrpcClientFactory.CreateGrpcService<IAuthenticationService>(invoker);
	}

	static LoginRequest ValidLoginRequest() =>
		new() { Email = "user@example.com", Password = "Password1", RememberMe = false };

	[Fact]
	async Task LockedOut_renders_identically_through_the_circuit_path_and_the_wire_path()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var host = await CreateHost(
			(_, _) => ValueTask.FromResult(Outcome<LoginResult>.Err(ErrorCategory.LockedOut, errors: new Dictionary<string, string[]> { [""] = ["locked"] })),
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
	async Task Wire_path_requests_are_validated_server_side()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var invoked = false;
		using var host = await CreateHost(
			(_, _) =>
			{
				invoked = true;
				return ValueTask.FromResult(Outcome<LoginResult>.Ok(new LoginResult()));
			},
			cancellationToken);

		var invalidRequest = new LoginRequest { Email = "", Password = "Password1" };
		var outcome = await CreateWireClient(host).Login(invalidRequest, cancellationToken);

		outcome.TryGetValue(out Failed failed).ShouldBeTrue();
		failed.Problem.Category.ShouldBe(ErrorCategory.Validation);
		failed.Problem.Errors.ShouldContainKey("Email");
		invoked.ShouldBeFalse();
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

		outcome.TryGetValue(out Success<LogoutResult> success).ShouldBeTrue();
		success.Value.ShouldBeOfType<LogoutResult>();
	}
}
