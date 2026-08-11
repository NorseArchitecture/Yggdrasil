using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Playwright;
using Norse.Hosting.BrowserTesting;

namespace Norse.Hosting.Stories.Server.Tests.BrowserRuntime;

public sealed class StoriesBrowserFixture : IAsyncLifetime
{
	readonly StoriesBrowserHostFixture _host = new();
	[SuppressMessage("Style", "IDE0032:Use auto property",
		Justification = "The backing field is required for volatile cross-thread browser callback reads.")]
	string _currentState = "<catalog boot>";

	internal Uri Origin => _host.Origin;

	internal string CurrentState
	{
		get => Volatile.Read(ref _currentState);
		set => Volatile.Write(
			ref _currentState,
			string.IsNullOrWhiteSpace(value) ? "<unknown>" : value);
	}

	public ValueTask InitializeAsync() => _host.InitializeAsync();

	internal Task<BrowserEvidence> OpenEvidenceAsync(string testName) =>
		_host.OpenEvidenceAsync(testName);

	internal StoriesBrowserEvidencePolicy CreateEvidencePolicy() => new(Origin);

	public ValueTask DisposeAsync() => _host.DisposeAsync();
}

sealed class StoriesBrowserHostFixture : BrowserHostFixture<Program>;

sealed class StoriesBrowserEvidencePolicy(Uri origin) :
	BrowserEvidencePolicy("Stories.Server exact scoped-CSS redirect")
{
	const string OverflowMessage =
		"Error: The custom event 'overflowchange' cannot have the same name as its browserEventName 'overflowchange'. Choose a different name for the custom event.";
	const string FluentUiModulePath =
		"/_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js:";
	internal const string RequestedStylesheetPath = "/Hosting.Stories.Client.styles.css";
	internal const string TargetStylesheetPath = "/Norse.Hosting.Stories.Client.styles.css";
	int _acceptedOverflowErrors;

	internal override bool IsExpectedPageError(string error)
	{
		if (_acceptedOverflowErrors >= 7 || !MatchesOverflowRegistrationError(error))
			return false;
		_acceptedOverflowErrors++;
		return true;
	}

	internal override bool IsExpectedRedirect(IResponse response) =>
		MatchesExpectedRedirect(response, origin);

	internal static bool MatchesExpectedRedirect(IResponse response, Uri expectedOrigin)
		=> response.Status == 302 &&
			response.Request.Method == "GET" &&
			IsExactFirstPartyPath(response.Url, expectedOrigin, RequestedStylesheetPath);

	internal static bool MatchesExpectedRedirectTarget(
		IResponse response,
		IRequest redirectSource,
		Uri expectedOrigin) =>
		response.Status == 200 &&
		response.Request.Method == "GET" &&
		ReferenceEquals(response.Request.RedirectedFrom, redirectSource) &&
		IsExactFirstPartyPath(response.Url, expectedOrigin, TargetStylesheetPath);

	internal static bool IsExactFirstPartyPath(string url, Uri expectedOrigin, string expectedPath) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
		string.Equals(
			uri.GetLeftPart(UriPartial.Authority),
			expectedOrigin.GetLeftPart(UriPartial.Authority),
			StringComparison.Ordinal) &&
		uri.AbsolutePath == expectedPath &&
		string.IsNullOrEmpty(uri.Query) &&
		string.IsNullOrEmpty(uri.Fragment);

	internal static bool MatchesOverflowRegistrationError(string error)
	{
		var lines = error.Split('\n');
		return lines.Length >= 3 &&
			lines[0].StartsWith("page-error page=http://127.0.0.1:", StringComparison.Ordinal) &&
			lines[0].EndsWith($": {OverflowMessage}", StringComparison.Ordinal) &&
			lines[1].StartsWith(
				"    at Object.registerCustomEventType (http://127.0.0.1:",
				StringComparison.Ordinal) &&
			lines[1].Contains("/_framework/blazor.webassembly.", StringComparison.Ordinal) &&
			lines[1].Contains(".js:", StringComparison.Ordinal) &&
			lines[2].StartsWith("    at Object.", StringComparison.Ordinal) &&
			lines[2].Contains(" [as Overflow] (http://127.0.0.1:", StringComparison.Ordinal) &&
			lines[2].Contains(FluentUiModulePath, StringComparison.Ordinal);
	}
}

sealed class StoriesRuntimeAudit : IDisposable
{
	readonly IPage _page;
	readonly Uri _origin;
	readonly Func<string> _currentState;
	readonly ConcurrentQueue<ResponseObservation> _responses = new();
	readonly ConcurrentQueue<RuntimeBootstrapObservation> _bootstraps = new();
	readonly ConcurrentQueue<NavigationObservation> _navigations = new();
	readonly ConcurrentQueue<RuntimeLifecycleObservation> _lifecycleEvents = new();
	readonly ConcurrentQueue<string> _diagnostics = new();
	readonly Lock _inventoryGate = new();
	IReadOnlyList<string> _lastInventory = [];
	string _lastPageUrl = "<not captured>";
	bool _disposed;

	internal StoriesRuntimeAudit(IPage page, Uri origin, Func<string> currentState)
	{
		_page = page;
		_origin = origin;
		_currentState = currentState;
		_page.Request += RecordRequest;
		_page.Response += RecordResponse;
		_page.FrameNavigated += RecordNavigation;
		_page.Console += RecordConsole;
	}

	internal int BootstrapCount => _bootstraps.Count;
	internal int MaxLiveFrameCount { get; private set; }

	internal RuntimeCheckpoint BeginCheckpoint() =>
		new(_navigations.Count, _bootstraps.Count, _lifecycleEvents.Count);

	internal RuntimeCheckpointEvidence AssertCheckpoint(
		RuntimeCheckpoint checkpoint,
		string name,
		RuntimeCheckpointLaw law)
	{
		var navigations = _navigations.ToArray()[checkpoint.NavigationCount..];
		var bootstraps = _bootstraps.ToArray()[checkpoint.BootstrapCount..];
		var lifecycleEvents = _lifecycleEvents.ToArray()[checkpoint.LifecycleEventCount..];
		RuntimeCheckpointPolicy.Assert(name, law, bootstraps, lifecycleEvents);

		return new(
			navigations.Length,
			bootstraps.Length,
			lifecycleEvents.Length,
			string.Join(" | ", navigations.Select(static observation =>
				$"{observation.Url} parent={observation.ParentUrl}")));
	}

	internal async Task CaptureFrameInventoryAsync(CancellationToken cancellationToken)
	{
		var state = _currentState();
		var frames = _page.Frames.ToArray();
		MaxLiveFrameCount = Math.Max(MaxLiveFrameCount, frames.Length);
		List<string> entries =
		[
			$"stories-current-state={state}",
			$"stories-page-url={_page.Url}",
			$"stories-live-frame-count={frames.Length}",
		];

		foreach (var frame in frames)
		{
			var url = frame.Url;
			var parentUrl = frame.ParentFrame?.Url ?? "<main>";
			var marker = await AwaitPlaywrightResultAsync(
				frame.Locator("body").GetAttributeAsync(
					"data-bs-parent-frame",
					new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
				cancellationToken);
			var classification = "outer";
			if (!ReferenceEquals(frame, _page.MainFrame))
			{
				var element = await AwaitPlaywrightResultAsync(frame.FrameElementAsync(), cancellationToken);
				classification = await AwaitPlaywrightResultAsync(
					element.EvaluateAsync<string>(
						"element => element.closest('.canvas-container') ? 'active' : 'pool'"),
					cancellationToken);
			}

			entries.Add(
				$"stories-inventory state={state} url={url} path={PathOf(url)} " +
				$"data-bs-parent-frame={marker ?? "<none>"} parent={parentUrl} " +
				$"parent-path={PathOf(parentUrl)} classification={classification}");
		}

		lock (_inventoryGate)
		{
			_lastPageUrl = _page.Url;
			_lastInventory = entries;
		}
	}

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "A diagnostic inventory must never replace the primary browser failure.")]
	internal async Task TryCaptureFrameInventoryAsync()
	{
		using var budget = new CancellationTokenSource(BrowserTimeouts.StoryState);
		try
		{
			await CaptureFrameInventoryAsync(budget.Token);
		}
		catch (Exception exception)
		{
			_diagnostics.Enqueue(
				$"stories-inventory-capture-failure state={_currentState()}: {exception}");
		}
	}

	internal int AssertRedirectLaw()
	{
		var responses = _responses.ToArray();
		var redirects = responses.Where(static response =>
			response.Status is >= 300 and <= 399 && response.Status != 304).ToArray();
		foreach (var redirect in redirects)
		{
			if (!redirect.ExpectedRedirect)
			{
				var observedTarget = responses.SingleOrDefault(response =>
					ReferenceEquals(response.Request.RedirectedFrom, redirect.Request));
				throw new BrowserFailure(
					$"Unexpected first-party redirect in state {redirect.State}: " +
					$"{redirect.Status} {redirect.Url} -> {observedTarget?.Url ?? "<none>"}.");
			}
		}

		var stylesheetRedirects = redirects.Where(static response =>
			response.ExpectedRedirect).ToArray();
		if (stylesheetRedirects.Length == 0)
			throw new BrowserFailure(
				$"Observed 0 exact {StoriesBrowserEvidencePolicy.RequestedStylesheetPath} redirects; required at least 1.");

		foreach (var redirect in stylesheetRedirects)
		{
			var final = responses.SingleOrDefault(response =>
				StoriesBrowserEvidencePolicy.MatchesExpectedRedirectTarget(
					response.Response,
					redirect.Request,
					_origin)) ?? throw new BrowserFailure(
				$"The expected stylesheet redirect in state {redirect.State} exposed no terminal response.");
		}

		return stylesheetRedirects.Length;
	}

	internal async Task AppendFailureEvidenceAsync(string testName)
	{
		var directory = Path.Combine(BrowserFailure.ArtifactRoot, testName);
		Directory.CreateDirectory(directory);
		List<string> entries;
		lock (_inventoryGate)
		{
			entries =
			[
				"stories-detailed-frame-inventory",
				$"stories-current-state={_currentState()}",
				$"stories-page-url={_lastPageUrl}",
				$"stories-max-live-frame-count={MaxLiveFrameCount}",
				.. _lastInventory,
			];
		}

		entries.AddRange(_bootstraps.Select(static request =>
			$"stories-bootstrap state={request.State} request={request.Url} path={PathOf(request.Url)} " +
			$"frame-url={request.FrameUrl} frame-path={PathOf(request.FrameUrl)}"));
		entries.AddRange(_diagnostics);
		await File.AppendAllLinesAsync(Path.Combine(directory, "frames.log"), entries);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_page.Request -= RecordRequest;
		_page.Response -= RecordResponse;
		_page.FrameNavigated -= RecordNavigation;
		_page.Console -= RecordConsole;
		_disposed = true;
	}

	void RecordRequest(object? _, IRequest request)
	{
		try
		{
			if (!TryGetFirstPartyUri(request.Url, out var uri) ||
				!uri.AbsolutePath.StartsWith("/_framework/blazor.webassembly", StringComparison.Ordinal))
				return;

			string frameUrl;
			bool isMainFrame;
			try
			{
				frameUrl = request.Frame.Url;
				isMainFrame = ReferenceEquals(request.Frame, _page.MainFrame);
			}
			catch (PlaywrightException exception)
			{
				frameUrl = $"<unavailable: {exception.Message}>";
				isMainFrame = false;
			}
			_bootstraps.Enqueue(new(_currentState(), request.Url, frameUrl, isMainFrame));
		}
		catch (Exception exception)
		{
			_diagnostics.Enqueue($"stories-bootstrap-callback-failure: {exception}");
		}
	}

	void RecordNavigation(object? _, IFrame frame)
	{
		try
		{
			_navigations.Enqueue(new(
				_currentState(),
				frame.Url,
				frame.ParentFrame?.Url ?? "<main>"));
		}
		catch (Exception exception)
		{
			_diagnostics.Enqueue($"stories-navigation-callback-failure: {exception}");
		}
	}

	void RecordConsole(object? _, IConsoleMessage message)
	{
		const string Prefix = "[NORSE-STORY-LIFECYCLE]";
		if (!message.Text.StartsWith(Prefix, StringComparison.Ordinal))
			return;
		if (RuntimeLifecycleObservation.TryParse(_currentState(), message.Text, out var observation))
			_lifecycleEvents.Enqueue(observation);
		else
			_lifecycleEvents.Enqueue(new(
				_currentState(),
				"<malformed>",
				message.Text,
				false));
	}

	void RecordResponse(object? _, IResponse response)
	{
		try
		{
			if (!IsFirstParty(response.Url))
				return;
			_responses.Enqueue(new(
				_currentState(),
				response,
				response.Status,
				response.Url,
				response.Request,
				StoriesBrowserEvidencePolicy.MatchesExpectedRedirect(response, _origin)));
		}
		catch (Exception exception)
		{
			_diagnostics.Enqueue($"stories-response-callback-failure: {exception}");
		}
	}

	bool TryGetFirstPartyUri(string url, [NotNullWhen(true)] out Uri? uri) =>
		Uri.TryCreate(url, UriKind.Absolute, out uri) &&
		string.Equals(
			uri.GetLeftPart(UriPartial.Authority),
			_origin.GetLeftPart(UriPartial.Authority),
			StringComparison.Ordinal);

	bool IsFirstParty(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
		string.Equals(
			uri.GetLeftPart(UriPartial.Authority),
			_origin.GetLeftPart(UriPartial.Authority),
			StringComparison.Ordinal);

	static string PathOf(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.PathAndQuery : "<unavailable>";

	async Task<T> AwaitPlaywrightResultAsync<T>(Task<T> action, CancellationToken cancellationToken)
	{
		await AwaitPlaywrightActionAsync(action, cancellationToken);
		return await action;
	}

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "Page-close and action failures are diagnostics on the primary cancellation.")]
	async Task AwaitPlaywrightActionAsync(Task action, CancellationToken cancellationToken)
	{
		try
		{
			await action.WaitAsync(cancellationToken);
		}
		catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
		{
			List<Exception> cleanupFailures = [];
			try
			{
				await _page.CloseAsync(new() { RunBeforeUnload = false });
			}
			catch (Exception cleanupFailure)
			{
				cleanupFailures.Add(cleanupFailure);
			}

			try
			{
				await action;
			}
			catch (Exception cleanupFailure)
			{
				cleanupFailures.Add(cleanupFailure);
			}

			if (cleanupFailures.Count > 0)
				exception.Data["Stories inventory cancellation cleanup"] = new AggregateException(
					"Stories inventory cancellation cleanup failed.",
					cleanupFailures);
			throw;
		}
	}

	sealed record ResponseObservation(
		string State,
		IResponse Response,
		int Status,
		string Url,
		IRequest Request,
		bool ExpectedRedirect);

	sealed record NavigationObservation(string State, string Url, string ParentUrl);
}

enum RuntimeCheckpointLaw
{
	ColdStartup,
	PinnedReentry,
	FullSweep,
}

static class RuntimeCheckpointPolicy
{
	internal static void Assert(
		string name,
		RuntimeCheckpointLaw law,
		IReadOnlyList<RuntimeBootstrapObservation> bootstraps,
		IReadOnlyList<RuntimeLifecycleObservation> lifecycleEvents)
	{
		AssertStructuralBootstrapLaw(name, bootstraps);
		switch (law)
		{
			case RuntimeCheckpointLaw.ColdStartup:
				AssertColdStartup(name, bootstraps, lifecycleEvents);
				break;
			case RuntimeCheckpointLaw.PinnedReentry:
				AssertZero(name, "bootstrap requests", bootstraps.Count);
				AssertZero(name, "lifecycle events", lifecycleEvents.Count);
				break;
			case RuntimeCheckpointLaw.FullSweep:
				AssertZero(name, "lifecycle events", lifecycleEvents.Count);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(law), law, "Unknown runtime checkpoint law.");
		}
	}

	static void AssertColdStartup(
		string name,
		IReadOnlyList<RuntimeBootstrapObservation> bootstraps,
		IReadOnlyList<RuntimeLifecycleObservation> lifecycleEvents)
	{
		var outerCount = bootstraps.Count(static observation => observation.IsMainFrame);
		var canvasCount = bootstraps.Count(static observation =>
			!observation.IsMainFrame && AbsolutePathOf(observation.FrameUrl) == "/iframe.html");
		if (bootstraps.Count != 2 || outerCount != 1 || canvasCount != 1)
			throw new BrowserFailure(
				$"Checkpoint {name} required exactly one outer and one /iframe.html canvas bootstrap; " +
				$"observed total={bootstraps.Count}, outer={outerCount}, canvas={canvasCount}.");

		AssertZero(name, "lifecycle events", lifecycleEvents.Count);
	}

	static void AssertStructuralBootstrapLaw(
		string name,
		IReadOnlyList<RuntimeBootstrapObservation> bootstraps)
	{
		var nestedBootstrap = bootstraps.FirstOrDefault(static observation =>
			!observation.IsMainFrame && AbsolutePathOf(observation.FrameUrl) != "/iframe.html");
		if (nestedBootstrap is not null)
			throw new BrowserFailure(
				$"Checkpoint {name} observed a nested catalog bootstrap owned by " +
				$"{nestedBootstrap.FrameUrl} in state {nestedBootstrap.State}.");
	}

	static void AssertZero(string name, string observation, int count)
	{
		if (count != 0)
			throw new BrowserFailure($"Checkpoint {name} required zero {observation}; observed {count}.");
	}

	static string AbsolutePathOf(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : "<unavailable>";
}

sealed record RuntimeBootstrapObservation(string State, string Url, string FrameUrl, bool IsMainFrame);

sealed record RuntimeLifecycleObservation(string State, string EventName, string Url, bool IsTopFrame)
{
	const string Prefix = "[NORSE-STORY-LIFECYCLE] ";

	internal static bool TryParse(
		string state,
		string text,
		[NotNullWhen(true)] out RuntimeLifecycleObservation? observation)
	{
		observation = null;
		if (!text.StartsWith(Prefix, StringComparison.Ordinal))
			return false;
		var fields = text[Prefix.Length..].Split(' ', 3, StringSplitOptions.None);
		if (fields.Length != 3 ||
			!fields[0].StartsWith("frame=", StringComparison.Ordinal) ||
			!fields[1].StartsWith("event=", StringComparison.Ordinal) ||
			!fields[2].StartsWith("url=", StringComparison.Ordinal))
			return false;

		var frame = fields[0]["frame=".Length..];
		if (frame is not ("top" or "child"))
			return false;
		var eventName = fields[1]["event=".Length..];
		var url = fields[2]["url=".Length..];
		if (eventName.Length == 0 || url.Length == 0)
			return false;

		observation = new(state, eventName, url, frame == "top");
		return true;
	}
}

readonly record struct RuntimeCheckpoint(int NavigationCount, int BootstrapCount, int LifecycleEventCount);

readonly record struct RuntimeCheckpointEvidence(
	int NavigationCount,
	int BootstrapCount,
	int LifecycleEventCount,
	string Navigations);
