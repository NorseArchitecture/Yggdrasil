using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Playwright;

namespace Norse.Hosting.BrowserTesting;

static class FrameworkRequestQuiescence
{
	internal const string CleanupDiagnosticKey = "FrameworkRequestQuiescence cleanup";

	internal static async Task WaitAsync(IPage page, Uri origin, CancellationToken cancellationToken)
		=> await WaitAsync(page, origin, "/", cancellationToken);

	internal static async Task WaitAsync(
		IPage page,
		Uri origin,
		string target,
		CancellationToken cancellationToken)
	{
		FrameworkRequestActivity activity = new(origin);
		var subscribed = true;

		void Started(object? _, IRequest request) => activity.Started(request);

		void Finished(object? _, IRequest request) => activity.Finished(request);

		void Responded(object? _, IResponse response) => activity.Responded(response);

		void Unsubscribe()
		{
			if (!subscribed)
				return;
			page.Request -= Started;
			page.RequestFinished -= Finished;
			page.RequestFailed -= Finished;
			page.Response -= Responded;
			subscribed = false;
		}

		page.Request += Started;
		page.RequestFinished += Finished;
		page.RequestFailed += Finished;
		page.Response += Responded;
		Task<IResponse?>? navigation = null;
		try
		{
			navigation = page.GotoAsync(target, new()
			{
				WaitUntil = WaitUntilState.DOMContentLoaded,
				Timeout = BrowserTimeouts.PlaywrightHostStartupMilliseconds,
			});
			await navigation.WaitAsync(cancellationToken);
			while (!activity.TryComplete(Unsubscribe))
				await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
		}
		catch (OperationCanceledException exception) when (
			cancellationToken.IsCancellationRequested && navigation is not null)
		{
			var cleanupFailures = await AbortNavigationAsync(page, navigation);
			if (cleanupFailures.Count > 0)
				exception.Data[CleanupDiagnosticKey] = new AggregateException(
					"Framework navigation cancellation cleanup failed.",
					cleanupFailures);
			throw;
		}
		finally
		{
			activity.Stop(Unsubscribe);
		}
	}

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "Page-close and navigation failures are diagnostics on the primary cancellation.")]
	static async Task<IReadOnlyList<Exception>> AbortNavigationAsync(IPage page, Task<IResponse?> navigation)
	{
		List<Exception> failures = [];
		try
		{
			await page.CloseAsync(new() { RunBeforeUnload = false });
		}
		catch (Exception exception)
		{
			failures.Add(exception);
		}

		try
		{
			await navigation;
		}
		catch (Exception exception)
		{
			failures.Add(exception);
		}
		return failures;
	}
}

sealed class FrameworkRequestActivity(Uri origin)
{
	static readonly TimeSpan _quietPeriod = TimeSpan.FromMilliseconds(500);
	readonly Lock _gate = new();
	readonly HashSet<IRequest> _pending = [];
	readonly string _authority = origin.GetLeftPart(UriPartial.Authority);
	bool _accepting = true;
	bool _sawSuccessfulWasm;
	long _lastActivity = Stopwatch.GetTimestamp();

	internal bool Started(IRequest request)
	{
		if (!IsFramework(request.Url))
			return false;

		lock (_gate)
		{
			if (!_accepting)
				return false;
			_pending.Add(request);
			_lastActivity = Stopwatch.GetTimestamp();
			return true;
		}
	}

	internal void Finished(IRequest request)
	{
		lock (_gate)
		{
			if (!_accepting || !_pending.Remove(request))
				return;
			_lastActivity = Stopwatch.GetTimestamp();
		}
	}

	internal void Responded(IResponse response)
	{
		if (!IsFramework(response.Url))
			return;

		lock (_gate)
		{
			if (!_accepting)
				return;
			_lastActivity = Stopwatch.GetTimestamp();
			if (response.Ok && Uri.TryCreate(response.Url, UriKind.Absolute, out var uri) &&
				uri.AbsolutePath.EndsWith(".wasm", StringComparison.Ordinal))
				_sawSuccessfulWasm = true;
		}
	}

	internal bool TryComplete(Action unsubscribe)
	{
		lock (_gate)
		{
			if (!_accepting)
				return true;
			if (!_sawSuccessfulWasm || _pending.Count != 0 ||
				Stopwatch.GetElapsedTime(_lastActivity) < _quietPeriod)
				return false;

			_accepting = false;
			unsubscribe();
			return true;
		}
	}

	internal void Stop(Action unsubscribe)
	{
		lock (_gate)
		{
			if (!_accepting)
				return;
			_accepting = false;
			unsubscribe();
		}
	}

	bool IsFramework(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
		string.Equals(uri.GetLeftPart(UriPartial.Authority), _authority, StringComparison.Ordinal) &&
		uri.AbsolutePath.StartsWith("/_framework/", StringComparison.Ordinal);
}
