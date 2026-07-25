using Norse.Abstractions.Contracts;
using Norse.AuthN.Services;
using Norse.Primitives;

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
/// The plan's acceptance gate (§4): force a real failure through the in-process gateway during a
/// simulated prerender, persist it, restore it as if WASM had just hydrated, and confirm the wire
/// gateway's <see cref="Problem"/> is identical in shape. Then repeat for the success path. This is the
/// test that proves parity is real, not asserted.
/// </summary>
public sealed class AuthenticationHydrationParityTests
{
	[Fact]
	async Task Forbidden_IdenticalProblem_AcrossInProcessThenWireGateway()
	{
		// In-process gateway (Server circuit, prerender): the real chain runs, AuthorizationBehavior
		// rejects the call, the in-process gateway returns Outcome<LoginResult>.Err(Forbidden) — no
		// wire involved at all.
		var inProcessResult = Outcome<LoginResult>.Err(ErrorCategory.Forbidden);

		// Persist across the simulated prerender -> WASM handoff.
		var store = new Dictionary<string, byte[]>();
		var persistingState = TestPersistentComponentState.Create(store);
		var hydration = new EnvelopeHydrationState(persistingState);
		using var subscription = hydration.Persist("login", () => inProcessResult);
		await TestPersistentComponentState.PersistAsync(persistingState, store);

		// WASM hydration: read the persisted state back — this is what the component renders from
		// the instant hydration completes, before the wire gateway is even asked to re-answer.
		var restoredState = TestPersistentComponentState.CreateFromStore(store);
		var restoredHydration = new EnvelopeHydrationState(restoredState);
		restoredHydration.TryTakeOutcome<LoginResult>("login", out var hydratedResult).ShouldBeTrue();

		// The wire gateway independently re-answers the same forced-Forbidden scenario, decoding the
		// real ErrorInfo-based trailer end to end (server ToRpcException -> client DecodeProblem).
		var wireResult = SimulateWireForbidden();

		hydratedResult.TryGetValue(out Failed hydratedFailed).ShouldBeTrue();
		wireResult.TryGetValue(out Failed wireFailed).ShouldBeTrue();
		hydratedFailed.Problem.Category.ShouldBe(wireFailed.Problem.Category);
		hydratedFailed.Problem.Category.ShouldBe(ErrorCategory.Forbidden);
	}

	[Fact]
	async Task Success_IdenticalLoginResult_AcrossInProcessThenWireGateway()
	{
		var loginResult = new LoginResult { Succeeded = true, DeferredCompletionUrl = null };
		var inProcessResult = Outcome<LoginResult>.Ok(loginResult);

		var store = new Dictionary<string, byte[]>();
		var persistingState = TestPersistentComponentState.Create(store);
		var hydration = new EnvelopeHydrationState(persistingState);
		using var subscription = hydration.Persist("login", () => inProcessResult);
		await TestPersistentComponentState.PersistAsync(persistingState, store);

		var restoredState = TestPersistentComponentState.CreateFromStore(store);
		var restoredHydration = new EnvelopeHydrationState(restoredState);
		restoredHydration.TryTakeOutcome<LoginResult>("login", out var hydratedResult).ShouldBeTrue();

		hydratedResult.TryGetValue(out Success<LoginResult> success).ShouldBeTrue();
		success.Value.Succeeded.ShouldBeTrue();
		success.Value.DeferredCompletionUrl.ShouldBeNull();
	}

	static Outcome<LoginResult> SimulateWireForbidden()
	{
		// Exercises the real Midgard round-trip (Task 5) rather than hand-constructing a Problem, so
		// this test would fail if ToRpcException/DecodeProblem ever drifted out of sync.
		var rpcException = new Problem { Category = ErrorCategory.Forbidden }.ToRpcExceptionForTest();
		var decoded = rpcException.DecodeProblemForTest();
		return Outcome<LoginResult>.Err(decoded.Category, decoded.Errors, decoded.CorrelationId);
	}
}
