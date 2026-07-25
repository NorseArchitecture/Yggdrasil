using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
/// Reusable harness around <see cref="ComponentStatePersistenceManager"/> — the same prerender-persist
/// / hydration-restore pairing <see cref="EnvelopeHydrationStateTests"/> originally established inline,
/// extracted so every test that only cares about the <see cref="PersistentComponentState"/> boundary
/// <see cref="EnvelopeHydrationState"/> consumes doesn't have to repeat the manager/renderer/store
/// plumbing. Each state handed back from <see cref="Create"/> is tracked to its owning manager so
/// <see cref="PersistAsync"/> can find it later — callers only ever hold the bare
/// <see cref="PersistentComponentState"/>, matching what <see cref="EnvelopeHydrationState"/>'s
/// constructor actually accepts.
/// </summary>
static class TestPersistentComponentState
{
	static readonly ConditionalWeakTable<PersistentComponentState, Registration> _registrations = [];

	/// <summary>Creates a fresh persisting-side state, not yet backed by <paramref name="store"/> — register callbacks against it, then flush with <see cref="PersistAsync"/>.</summary>
	public static PersistentComponentState Create(Dictionary<string, byte[]> store)
	{
		var manager = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
		_registrations.Add(manager.State, new Registration(manager, store));
		return manager.State;
	}

	/// <summary>Runs every callback registered against <paramref name="state"/> and flushes the result into <paramref name="store"/> — the simulated prerender -&gt; hydration hand-off.</summary>
	public static async Task PersistAsync(PersistentComponentState state, Dictionary<string, byte[]> store)
	{
		if (!_registrations.TryGetValue(state, out var registration))
			throw new InvalidOperationException($"{nameof(PersistentComponentState)} was not created via {nameof(TestPersistentComponentState)}.{nameof(Create)}.");
		if (!ReferenceEquals(registration.Store, store))
			throw new InvalidOperationException($"{nameof(store)} must be the same instance passed to {nameof(Create)} for this state.");

		using var renderer = new TestRenderer();
		await registration.Manager.PersistStateAsync(new TestPersistentComponentStateStore(store), renderer);
	}

	/// <summary>Restores a fresh state from <paramref name="store"/> — the WASM-side half of the round-trip. Restoration always completes synchronously against the in-memory test store, so callers don't need to await it.</summary>
	public static PersistentComponentState CreateFromStore(Dictionary<string, byte[]> store)
	{
		var manager = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
		manager.RestoreStateAsync(new TestPersistentComponentStateStore(store)).GetAwaiter().GetResult();
		return manager.State;
	}

	/// <summary>Ties a persisting-side manager to the store it was created against, so <see cref="PersistAsync"/> can fail loudly on a mismatched store instead of silently persisting into the wrong one.</summary>
	sealed record Registration(ComponentStatePersistenceManager Manager, Dictionary<string, byte[]> Store);
}
