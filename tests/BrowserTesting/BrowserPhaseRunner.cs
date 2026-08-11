using System.Diagnostics;

namespace Norse.Hosting.BrowserTesting;

sealed class BrowserPhaseRunner(CancellationToken aggregateToken)
{
	internal string? CurrentPhase { get; private set; }
	internal string? TimedOutPhase { get; private set; }

	internal async Task RunAsync(
		string phase,
		TimeSpan budget,
		Func<CancellationToken, Task> action)
	{
		using var phaseBudget = new CancellationTokenSource(budget);
		using var actionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
			aggregateToken,
			phaseBudget.Token);
		var elapsed = Stopwatch.StartNew();
		CurrentPhase = phase;
		try
		{
			await action(actionCancellation.Token);
		}
		catch (OperationCanceledException exception) when (actionCancellation.IsCancellationRequested)
		{
			if (aggregateToken.IsCancellationRequested)
				throw BrowserFailure.AggregateTimeoutDuringPhase(
					phase,
					elapsed.Elapsed,
					budget,
					phaseBudget.IsCancellationRequested,
					exception);

			TimedOutPhase = phase;
			throw BrowserFailure.PhaseTimeout(phase, elapsed.Elapsed, budget, exception);
		}
		finally
		{
			CurrentPhase = null;
		}
	}

	internal void ThrowIfAggregateExpired()
	{
		if (aggregateToken.IsCancellationRequested)
			throw BrowserFailure.AggregateTimeout();
	}
}
