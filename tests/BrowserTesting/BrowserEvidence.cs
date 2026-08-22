using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Playwright;

namespace Norse.Hosting.BrowserTesting;

sealed class BrowserEvidence
{
	internal const string OperationFailureCleanupDiagnosticKey =
		"BrowserEvidence.OperationFailureCleanup";

	readonly IBrowserContext _context;
	readonly IPage _page;
	readonly string _testName;
	readonly Uri _origin;
	readonly ConcurrentQueue<string> _serverLog;
	readonly Func<IResponse, bool> _expectedRedirect;
	readonly BrowserPhaseRunner _phaseRunner;
	readonly Lock _eventGate = new();
	readonly ConcurrentQueue<string> _browserLog = new();
	readonly ConcurrentQueue<string> _pageErrors = new();
	readonly ConcurrentQueue<string> _consoleErrors = new();
	readonly ConcurrentQueue<string> _callbackFailures = new();
	readonly ConcurrentQueue<string> _evidenceFailures = new();
	readonly ConcurrentQueue<string> _networkLog = new();
	readonly ConcurrentQueue<IRequest> _failedRequests = new();
	readonly ConcurrentQueue<IResponse> _responses = new();
	readonly ConcurrentQueue<string> _frameLog = new();
	IReadOnlyList<string> _frameSnapshot = [];
	byte[] _screenshot = [];
	bool _acceptingEvents = true;
	bool _eventsFrozen;
	bool _traceStopped;
	bool _closing;
	bool _closed;
	int _executionStarted;

	BrowserEvidence(
		IBrowserContext context,
		IPage page,
		string testName,
		Uri origin,
		ConcurrentQueue<string> serverLog,
		Func<IResponse, bool> expectedRedirect,
		CancellationToken aggregateToken)
	{
		_context = context;
		_page = page;
		_testName = testName;
		_origin = origin;
		_serverLog = serverLog;
		_expectedRedirect = expectedRedirect;
		_phaseRunner = new(aggregateToken);

		_page.Console += RecordConsole;
		_page.PageError += RecordPageError;
		_page.RequestFailed += RecordRequestFailed;
		_page.Response += RecordResponse;
		_page.FrameAttached += RecordFrameAttached;
		_page.FrameNavigated += RecordFrameNavigated;
	}

	/// <summary>
	///     Seeds the browser context's cookie jar before the first navigation -- the only way a Playwright
	///     context can carry an already-minted cookie (e.g. one read back from a prior <see cref="HttpClient" />
	///     handshake against the same host) instead of minting its own on first contact. Safe to call any
	///     time before <see cref="ExecuteAsync" />'s first <c>page.GotoAsync</c>: cookies live in the
	///     context's jar, not the already-created (still blank) page.
	/// </summary>
	internal Task AddCookiesAsync(IEnumerable<Microsoft.Playwright.Cookie> cookies) =>
		_context.AddCookiesAsync(cookies);

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "A partially started evidence context must always be disposed while preserving both failures.")]
	internal static async Task<BrowserEvidence> StartAsync(
		IBrowserContext context,
		string testName,
		Uri origin,
		ConcurrentQueue<string> serverLog,
		Func<IResponse, bool> expectedRedirect,
		CancellationToken aggregateToken)
	{
		try
		{
			BrowserFailure.RemovePriorArtifacts(testName);
			await context.Tracing.StartAsync(new()
			{
				Screenshots = true,
				Snapshots = true,
				Sources = true,
			});
			var page = await context.NewPageAsync();
			return new(context, page, testName, origin, serverLog, expectedRedirect, aggregateToken);
		}
		catch (Exception startupException)
		{
			try
			{
				await context.DisposeAsync();
			}
			catch (Exception cleanupException)
			{
				throw new AggregateException(
					"Browser evidence startup and context cleanup both failed.",
					startupException,
					cleanupException);
			}
			throw;
		}
	}

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "The original test failure must remain primary while evidence flushing is best effort.")]
	internal async Task ExecuteAsync(
		Func<BrowserOperation, Task> action,
		BrowserEvidencePolicy? policy = null)
	{
		if (Interlocked.Exchange(ref _executionStarted, 1) != 0)
			throw new InvalidOperationException("Browser evidence has already executed.");

		var operationActive = 1;
		BrowserOperation operation = new(
			_page,
			_phaseRunner,
			() => Volatile.Read(ref operationActive) != 0);
		try
		{
			await action(operation);
		}
		catch (Exception exception)
		{
			Interlocked.Exchange(ref operationActive, 0);
			_browserLog.Enqueue($"Browser test operation failed: {exception}");
			var artifacts = await FlushFailureEvidenceAsync();
			if (artifacts.Failures.Count > 0)
			{
				exception.Data[OperationFailureCleanupDiagnosticKey] = new AggregateException(
					"Browser evidence collection or cleanup also failed after the primary operation failure.",
					artifacts.Failures);
			}
			throw;
		}
		Interlocked.Exchange(ref operationActive, 0);

		try
		{
			await CompleteAsync(policy);
		}
		catch (Exception exception)
		{
			_browserLog.Enqueue($"Browser evidence completion failed: {exception}");
			await FlushFailureEvidenceAsync();
			throw;
		}
	}

	async Task CompleteAsync(BrowserEvidencePolicy? policy)
	{
		if (_closed || _closing)
			throw new InvalidOperationException("Browser evidence is already closed.");

		_closing = true;
		try
		{
			BrowserFailure? failure = null;
			try
			{
				_phaseRunner.ThrowIfAggregateExpired();
			}
			catch (BrowserFailure exception)
			{
				failure = exception;
			}

			await PrepareFinalSnapshotAsync();
			var failures = CollectFailures(policy);
			if (failure is null && failures.Count > 0)
			{
				var directory = Path.Combine(BrowserFailure.ArtifactRoot, _testName);
				failure = new BrowserFailure(
					$"Browser evidence detected {failures.Count.ToString(CultureInfo.InvariantCulture)} failure(s): " +
					$"{string.Join("; ", failures)}. Evidence: {directory}");
			}

			if (failure is null)
			{
				List<Exception> cleanupFailures = [];
				await StopTraceAsync(path: null, cleanupFailures);
				await CloseContextAsync(cleanupFailures);
				if (cleanupFailures.Count == 0)
					return;

				foreach (var cleanupFailure in cleanupFailures)
					RecordEvidenceFailure("successful finalization", cleanupFailure);
				var failedFinalization = await WriteFailureArtifactsAsync();
				throw new BrowserFailure(
					$"Browser evidence finalization failed. Evidence: {failedFinalization.Directory}",
					new AggregateException(cleanupFailures.Concat(failedFinalization.Failures)));
			}

			var artifacts = await WriteFailureArtifactsAsync();
			var message = failure.Message.Contains(artifacts.Directory, StringComparison.Ordinal) ?
				failure.Message :
				$"{failure.Message} Evidence: {artifacts.Directory}";
			if (artifacts.Failures.Count == 0 && string.Equals(message, failure.Message, StringComparison.Ordinal))
				throw failure;

			Exception innerException = artifacts.Failures.Count == 0 ?
				failure :
				new AggregateException(new[] { failure }.Concat(artifacts.Failures));
			throw new BrowserFailure(message, innerException);
		}
		finally
		{
			_closing = false;
		}
	}

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "Evidence flushing is best effort and must never replace the primary test or completion failure.")]
	async Task<ArtifactWriteResult> FlushFailureEvidenceAsync()
	{
		var directory = Path.Combine(BrowserFailure.ArtifactRoot, _testName);
		if (_closed || _closing)
			return new(directory, []);

		_closing = true;
		try
		{
			return await WriteFailureArtifactsAsync();
		}
		catch (Exception exception)
		{
			RecordEvidenceFailure("best-effort failure flush", exception);
			List<Exception> failures = [exception];
			var cleanupFailureStart = failures.Count;
			await CloseContextAsync(failures);
			var cleanupFailures = failures.Skip(cleanupFailureStart).ToArray();
			foreach (var cleanupFailure in cleanupFailures)
				RecordEvidenceFailure("best-effort context close", cleanupFailure);
			if (cleanupFailures.Length > 0)
			{
				await AppendLinesAsync(
					Path.Combine(directory, "browser.log"),
					cleanupFailures.Select(static failure => $"Evidence context shutdown failed: {failure}"),
					failures);
			}
			return new(directory, failures);
		}
		finally
		{
			_closing = false;
		}
	}

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "A faulty host policy is evidence failure, not a callback-thread exception.")]
	List<string> CollectFailures(BrowserEvidencePolicy? policy)
	{
		List<string> failures = [];
		var unexpectedPageErrorCount = 0;
		foreach (var pageError in _pageErrors)
		{
			try
			{
				if (policy?.IsExpectedPageError(pageError) is true)
					_browserLog.Enqueue($"Evidence policy '{policy.Name}' accepted a known page error.");
				else
					unexpectedPageErrorCount++;
			}
			catch (Exception exception)
			{
				unexpectedPageErrorCount++;
				RecordEvidenceFailure("page-error policy", exception);
			}
		}
		if (unexpectedPageErrorCount > 0)
			failures.Add($"{unexpectedPageErrorCount.ToString(CultureInfo.InvariantCulture)} uncaught page error(s)");
		if (!_consoleErrors.IsEmpty)
			failures.Add($"{_consoleErrors.Count.ToString(CultureInfo.InvariantCulture)} error-severity console entry/entries");

		var unexpectedRequestFailureCount = 0;
		foreach (var request in _failedRequests.Where(request => IsFirstParty(request.Url)))
		{
			try
			{
				if (policy?.IsExpectedRequestFailure(request) is true)
					_browserLog.Enqueue($"Evidence policy '{policy.Name}' accepted a known request failure.");
				else
					unexpectedRequestFailureCount++;
			}
			catch (Exception exception)
			{
				unexpectedRequestFailureCount++;
				RecordEvidenceFailure("request-failure policy", exception);
			}
		}
		if (unexpectedRequestFailureCount > 0)
			failures.Add($"{unexpectedRequestFailureCount.ToString(CultureInfo.InvariantCulture)} failed first-party request(s)");

		var responses = _responses.Where(response => IsFirstParty(response.Url)).ToArray();
		var failedResponses = responses.Where(static response => response.Status is >= 400 and <= 599).ToArray();
		if (failedResponses.Length > 0)
			failures.Add($"{failedResponses.Length.ToString(CultureInfo.InvariantCulture)} first-party HTTP error response(s)");

		var unexpectedRedirectCount = 0;
		foreach (var response in responses.Where(static response =>
			response.Status is >= 300 and <= 399 && response.Status != 304))
		{
			try
			{
				var policyAccepted = policy?.IsExpectedRedirect(response) is true;
				if (_expectedRedirect(response) || policyAccepted)
				{
					if (policyAccepted)
						_browserLog.Enqueue($"Evidence policy '{policy!.Name}' accepted a known redirect.");
				}
				else
					unexpectedRedirectCount++;
			}
			catch (Exception exception)
			{
				unexpectedRedirectCount++;
				RecordEvidenceFailure("expected redirect predicate", exception);
			}
		}
		if (unexpectedRedirectCount > 0)
			failures.Add($"{unexpectedRedirectCount.ToString(CultureInfo.InvariantCulture)} unexpected first-party redirect(s)");
		if (!_callbackFailures.IsEmpty)
			failures.Add($"{_callbackFailures.Count.ToString(CultureInfo.InvariantCulture)} evidence callback recording failure(s)");
		if (!_evidenceFailures.IsEmpty)
			failures.Add($"{_evidenceFailures.Count.ToString(CultureInfo.InvariantCulture)} evidence collection failure(s)");
		return failures;
	}

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "Every evidence write and context close is attempted independently before failures are reported.")]
	async Task<ArtifactWriteResult> WriteFailureArtifactsAsync()
	{
		await PrepareFinalSnapshotAsync();
		var directory = Path.Combine(BrowserFailure.ArtifactRoot, _testName);
		List<Exception> failures = [];
		try
		{
			Directory.CreateDirectory(directory);
		}
		catch (Exception exception)
		{
			failures.Add(exception);
		}

		await WriteBytesAsync(Path.Combine(directory, "page.png"), _screenshot, failures);
		var tracePath = Path.Combine(directory, "trace.zip");
		await StopTraceAsync(tracePath, failures);
		if (!File.Exists(tracePath))
			await WriteBytesAsync(tracePath, [], failures);
		await WriteLinesAsync(Path.Combine(directory, "network.log"), _networkLog, failures);
		await WriteLinesAsync(Path.Combine(directory, "server.log"), _serverLog, failures);
		await WriteLinesAsync(Path.Combine(directory, "frames.log"), _frameSnapshot, failures);
		foreach (var failure in failures)
			_browserLog.Enqueue($"Evidence artifact operation failed: {failure}");
		var browserLogPath = Path.Combine(directory, "browser.log");
		await WriteLinesAsync(browserLogPath, _browserLog, failures);
		var cleanupFailureStart = failures.Count;
		await CloseContextAsync(failures);
		var cleanupFailures = failures.Skip(cleanupFailureStart).ToArray();
		foreach (var cleanupFailure in cleanupFailures)
			_browserLog.Enqueue($"Evidence context shutdown failed: {cleanupFailure}");
		if (cleanupFailures.Length > 0)
		{
			await AppendLinesAsync(
				browserLogPath,
				cleanupFailures.Select(static failure => $"Evidence context shutdown failed: {failure}"),
				failures);
		}
		return new(directory, failures);
	}

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "Final evidence capture records every failure and still freezes the event stream.")]
	async Task PrepareFinalSnapshotAsync()
	{
		if (_eventsFrozen)
			return;

		try
		{
			_screenshot = await _page.ScreenshotAsync(new() { FullPage = true });
		}
		catch (Exception exception)
		{
			RecordEvidenceFailure("page screenshot", exception);
		}

		try
		{
			_frameSnapshot = await CaptureFrameSnapshotAsync();
		}
		catch (Exception exception)
		{
			RecordEvidenceFailure("frame inventory", exception);
		}

		try
		{
			await _page.CloseAsync();
		}
		catch (Exception exception)
		{
			RecordEvidenceFailure("page close", exception);
		}
		finally
		{
			FreezeEvents();
		}
	}

	async Task<IReadOnlyList<string>> CaptureFrameSnapshotAsync()
	{
		List<string> entries = [.. _frameLog];
		foreach (var frame in _page.Frames)
		{
			var parent = frame.ParentFrame?.Url ?? "<main>";
			string? marker;
			try
			{
				marker = await frame.Locator("body").GetAttributeAsync("data-bs-parent-frame");
			}
			catch (PlaywrightException exception)
			{
				marker = $"<unavailable: {exception.Message}>";
			}
			entries.Add($"inventory url={frame.Url} parent={parent} data-bs-parent-frame={marker ?? "<none>"}");
		}
		return entries;
	}

	void FreezeEvents()
	{
		lock (_eventGate)
		{
			if (_eventsFrozen)
				return;
			_acceptingEvents = false;
			Unsubscribe();
			_eventsFrozen = true;
		}
	}

	void Unsubscribe()
	{
		_page.Console -= RecordConsole;
		_page.PageError -= RecordPageError;
		_page.RequestFailed -= RecordRequestFailed;
		_page.Response -= RecordResponse;
		_page.FrameAttached -= RecordFrameAttached;
		_page.FrameNavigated -= RecordFrameNavigated;
	}

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "Cleanup failures are accumulated so later cleanup always runs.")]
	async Task StopTraceAsync(string? path, List<Exception> failures)
	{
		if (_traceStopped)
			return;
		try
		{
			await _context.Tracing.StopAsync(path is null ? null : new() { Path = path });
			_traceStopped = true;
		}
		catch (Exception exception)
		{
			failures.Add(exception);
		}
	}

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "The context remains retryable unless disposal actually succeeds.")]
	async Task CloseContextAsync(List<Exception> failures)
	{
		if (_closed)
			return;
		try
		{
			await _context.DisposeAsync();
			_closed = true;
		}
		catch (Exception exception)
		{
			failures.Add(exception);
		}
	}

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "Artifact writes are independent so one failed file cannot prevent context cleanup.")]
	static async Task WriteBytesAsync(string path, byte[] contents, List<Exception> failures)
	{
		try
		{
			await File.WriteAllBytesAsync(path, contents);
		}
		catch (Exception exception)
		{
			failures.Add(exception);
		}
	}

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "Artifact writes are independent so one failed file cannot prevent context cleanup.")]
	static async Task WriteLinesAsync(
		string path,
		IEnumerable<string> contents,
		List<Exception> failures)
	{
		try
		{
			await File.WriteAllLinesAsync(path, contents);
		}
		catch (Exception exception)
		{
			failures.Add(exception);
		}
	}

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "A diagnostic append is best effort and must never mask the primary test failure.")]
	static async Task AppendLinesAsync(
		string path,
		IEnumerable<string> contents,
		List<Exception> failures)
	{
		try
		{
			await File.AppendAllLinesAsync(path, contents);
		}
		catch (Exception exception)
		{
			failures.Add(exception);
		}
	}

	void RecordConsole(object? _, IConsoleMessage message) => RecordSafely("console", () =>
	{
		var entry = $"console[{message.Type}] page={message.Page?.Url ?? "<none>"} location={message.Location}: {message.Text}";
		_browserLog.Enqueue(entry);
		if (string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
			_consoleErrors.Enqueue(entry);
	});

	void RecordPageError(object? _, string error) => RecordSafely("page error", () =>
	{
		var entry = $"page-error page={_page.Url}: {error}";
		_browserLog.Enqueue(entry);
		_pageErrors.Enqueue(entry);
	});

	void RecordRequestFailed(object? _, IRequest request) => RecordSafely("failed request", () =>
	{
		_failedRequests.Enqueue(request);
		_networkLog.Enqueue($"failed {request.Method} {request.Url}: {request.Failure ?? "unknown failure"}");
	});

	void RecordResponse(object? _, IResponse response) => RecordSafely("response", () =>
	{
		_responses.Enqueue(response);
		var redirectTarget = response.Request.RedirectedTo?.Url;
		_networkLog.Enqueue(redirectTarget is null ?
			$"response {response.Status.ToString(CultureInfo.InvariantCulture)} {response.Request.Method} {response.Url}" :
			$"redirect {response.Status.ToString(CultureInfo.InvariantCulture)} {response.Url} -> {redirectTarget}");
	});

	void RecordFrameAttached(object? _, IFrame frame) => RecordSafely("frame attached", () =>
		_frameLog.Enqueue($"attached url={frame.Url} parent={frame.ParentFrame?.Url ?? "<main>"}"));

	void RecordFrameNavigated(object? _, IFrame frame) => RecordSafely("frame navigated", () =>
		_frameLog.Enqueue($"navigated url={frame.Url} parent={frame.ParentFrame?.Url ?? "<main>"}"));

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "Playwright event callbacks must record collection failures without throwing on the event thread.")]
	void RecordSafely(string source, Action record)
	{
		lock (_eventGate)
		{
			if (!_acceptingEvents)
				return;
			try
			{
				record();
			}
			catch (Exception exception)
			{
				var entry = $"Evidence callback '{source}' failed to record: {exception}";
				_browserLog.Enqueue(entry);
				_callbackFailures.Enqueue(entry);
			}
		}
	}

	void RecordEvidenceFailure(string operation, Exception exception)
	{
		var entry = $"Evidence operation '{operation}' failed: {exception}";
		_browserLog.Enqueue(entry);
		_evidenceFailures.Enqueue(entry);
	}

	bool IsFirstParty(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
		string.Equals(uri.GetLeftPart(UriPartial.Authority), _origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);

	internal sealed class BrowserOperation
	{
		[SuppressMessage("Style", "IDE0032:Use auto property",
			Justification = "The guarded property rejects use after the ExecuteAsync scope closes.")]
		readonly IPage _page;
		readonly BrowserPhaseRunner _phaseRunner;
		readonly Func<bool> _isActive;

		internal BrowserOperation(
			IPage page,
			BrowserPhaseRunner phaseRunner,
			Func<bool> isActive)
		{
			_page = page;
			_phaseRunner = phaseRunner;
			_isActive = isActive;
		}

		internal IPage Page
		{
			get
			{
				EnsureActive();
				return _page;
			}
		}

		internal async Task RunPhaseAsync(
			string phase,
			TimeSpan budget,
			Func<CancellationToken, Task> action)
		{
			EnsureActive();
			_phaseRunner.ThrowIfAggregateExpired();
			await _phaseRunner.RunAsync(phase, budget, action);
		}

		void EnsureActive()
		{
			if (!_isActive())
				throw new InvalidOperationException("The browser operation is available only during ExecuteAsync.");
		}
	}

	sealed record ArtifactWriteResult(string Directory, IReadOnlyList<Exception> Failures);
}

abstract class BrowserEvidencePolicy(string name)
{
	internal string Name { get; } = string.IsNullOrWhiteSpace(name) ?
		throw new ArgumentException("An evidence policy requires a name.", nameof(name)) :
		name;

	internal virtual bool IsExpectedPageError(string error) => false;
	internal virtual bool IsExpectedRequestFailure(IRequest request) => false;
	internal virtual bool IsExpectedRedirect(IResponse response) => false;
}
