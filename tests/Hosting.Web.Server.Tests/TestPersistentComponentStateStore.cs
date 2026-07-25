using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Infrastructure;

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
/// Minimal in-memory <see cref="IPersistentComponentStateStore"/> backed by a shared dictionary — the
/// same in-process store instance simulates the prerender-to-hydration hand-off: one
/// <see cref="ComponentStatePersistenceManager"/> persists into it, a second one restores from it,
/// exactly as the real Blazor Server → WASM boundary does across the wire in production.
/// </summary>
sealed class TestPersistentComponentStateStore(Dictionary<string, byte[]> store) : IPersistentComponentStateStore
{
	public Task<IDictionary<string, byte[]>> GetPersistedStateAsync() =>
		Task.FromResult<IDictionary<string, byte[]>>(new Dictionary<string, byte[]>(store, StringComparer.Ordinal));

	public Task PersistStateAsync(IReadOnlyDictionary<string, byte[]> state)
	{
		store.Clear();
		foreach (var (key, value) in state)
			store[key] = value;

		return Task.CompletedTask;
	}
}
