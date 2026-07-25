using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Norse.Abstractions.Contracts;
using Norse.Primitives;

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
/// Round-trips a whole <see cref="Outcome{T}"/> — both cases — through the same public
/// <see cref="ComponentStatePersistenceManager"/> pipeline Blazor Server itself uses to hand state
/// from prerender to hydration: one manager persists into a shared in-memory store, a second one
/// (standing in for the WASM-side circuit) restores from it, and <see cref="EnvelopeHydrationState"/>
/// reconstructs the original <see cref="Outcome{T}"/> on the far side without ever putting the
/// union's private layout on the wire.
/// </summary>
public sealed class EnvelopeHydrationStateTests
{
	[Fact]
	async Task Persist_ThenTryTake_RoundTripsSuccessCase()
	{
		var store = new Dictionary<string, byte[]>();

		var persistingManager = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
		var hydration = new EnvelopeHydrationState(persistingManager.State);

		using var subscription = hydration.Persist("login", () => Outcome<bool>.Ok(true));
		using (var renderer = new TestRenderer())
			await persistingManager.PersistStateAsync(new TestPersistentComponentStateStore(store), renderer);

		var restoringManager = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
		await restoringManager.RestoreStateAsync(new TestPersistentComponentStateStore(store));
		var restoredHydration = new EnvelopeHydrationState(restoringManager.State);

		restoredHydration.TryTakeOutcome<bool>("login", out var outcome).ShouldBeTrue();
		outcome.TryGetValue(out Success<bool> success).ShouldBeTrue();
		success.Value.ShouldBeTrue();
	}

	[Fact]
	async Task Persist_ThenTryTake_RoundTripsFailureCase_CategoryAndErrors()
	{
		var store = new Dictionary<string, byte[]>();

		var persistingManager = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
		var hydration = new EnvelopeHydrationState(persistingManager.State);

		using var subscription = hydration.Persist("login", () =>
			Outcome<bool>.Err(ErrorCategory.Forbidden, new Dictionary<string, string[]> { [""] = ["nope"] }));
		using (var renderer = new TestRenderer())
			await persistingManager.PersistStateAsync(new TestPersistentComponentStateStore(store), renderer);

		var restoringManager = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
		await restoringManager.RestoreStateAsync(new TestPersistentComponentStateStore(store));
		var restoredHydration = new EnvelopeHydrationState(restoringManager.State);

		restoredHydration.TryTakeOutcome<bool>("login", out var outcome).ShouldBeTrue();
		outcome.TryGetValue(out Failed failed).ShouldBeTrue();
		failed.Problem.Category.ShouldBe(ErrorCategory.Forbidden);
		failed.Problem.Errors[""].ShouldBe(["nope"]);
	}
}
