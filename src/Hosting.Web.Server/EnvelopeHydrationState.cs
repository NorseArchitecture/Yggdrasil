using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Norse.Abstractions.Contracts;
using Norse.Primitives;

namespace Norse.Hosting.Web.Server;

/// <summary>
/// Persists a whole <see cref="Outcome{T}"/> — success or failure — across the prerender-to-WASM
/// hydration boundary (spec §3, decided law item 6), so a failure discovered during prerender
/// re-renders identically once WASM takes over instead of flashing to a loading state. First use of
/// <see cref="PersistentComponentState"/> in this codebase — genuinely new wiring, not an extension of
/// an existing pattern. The union's private layout never crosses JSON directly; <see cref="EnvelopeDto{T}"/>
/// is the wire-safe transfer shape, reconstructed into a real <see cref="Outcome{T}"/> on the way back.
/// </summary>
public sealed class EnvelopeHydrationState(PersistentComponentState state)
{
	/// <summary>Registers a callback that persists the outcome of <paramref name="outcomeFactory"/> under <paramref name="key"/> just before prerender state is serialized.</summary>
	public PersistingComponentStateSubscription Persist<T>(string key, Func<Outcome<T>> outcomeFactory) where T : notnull =>
		state.RegisterOnPersisting(() =>
		{
			var dto = outcomeFactory() switch
			{
				Success<T>(var value) => new EnvelopeDto<T>(true, value, null),
				Failed(var problem) => new EnvelopeDto<T>(false, default, problem),
			};
			state.PersistAsJson(key, dto);
			return Task.CompletedTask;
		});

	/// <summary>Reconstructs the persisted <see cref="Outcome{T}"/> for <paramref name="key"/>, if present.</summary>
	public bool TryTakeOutcome<T>(string key, [MaybeNullWhen(false)] out Outcome<T> outcome) where T : notnull
	{
		if (state.TryTakeFromJson<EnvelopeDto<T>>(key, out var dto) && dto is not null)
		{
			outcome = dto.IsSuccess
				? Outcome<T>.Ok(dto.Value!)
				: Outcome<T>.Err(dto.Problem!.Category, dto.Problem.Errors, dto.Problem.CorrelationId);
			return true;
		}
		outcome = default!;
		return false;
	}

	sealed record EnvelopeDto<T>(bool IsSuccess, T? Value, Problem? Problem);
}
