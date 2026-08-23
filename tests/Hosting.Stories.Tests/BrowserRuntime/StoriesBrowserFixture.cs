using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Playwright;
using Norse.Hosting.BrowserTesting;

namespace Norse.Hosting.Stories.Tests.BrowserRuntime;

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

	internal static StoriesBrowserEvidencePolicy CreateEvidencePolicy() => new();

	public ValueTask DisposeAsync() => _host.DisposeAsync();
}

sealed class StoriesBrowserHostFixture : BrowserHostFixture<Program>;

// Confirmed live (Task 6, 2026-08-22): the scoped-CSS redirect this policy used to allow no longer
// occurs at all. Under WASM mode, the requested "Hosting.Stories.Client.styles.css" link didn't
// carry the brand prefix and 302'd to "Norse.Hosting.Stories.Client.styles.css". Under Interactive
// Server, IFramePage.razor references the bundle through @Assets[...], which resolves straight to
// its fingerprinted path (e.g. "Norse.Hosting.Stories.{hash}.styles.css") with zero redirects --
// live-verified against the running host across an outer load and a multi-canvas Docs page. The
// base BrowserEvidence collector already fails on any *unaccepted* first-party redirect, so once
// this policy accepts none, an unexpected regression back to a redirecting reference still fails
// loudly; there is no longer a dedicated law to encode here.
sealed class StoriesBrowserEvidencePolicy() :
	BrowserEvidencePolicy("Stories FluentUI custom-event registration allowance")
{
	// Confirmed live (Task 6, 2026-08-22): under Interactive Server, FluentUI's JS module
	// double-registers two custom DOM events -- 'overflowchange' and 'accordionchange' -- once each
	// per top-level Blazor Web document/circuit it attaches to (the outer catalog shell and every
	// canvas iframe alike), because its module's afterWebStarted (SSR/prerender pass) and
	// afterServerStarted (interactive pass) lifecycle callbacks both fire on the same page load.
	// Reused (pooled) canvases that client-navigate in place, rather than mounting a fresh document,
	// do not repeat this. This predates any Stories-authored FluentUI markup (BlazingStory ships
	// none of its own either) and is cosmetic -- it never blocked rendering or interactivity in
	// live verification. See Task 4's findings note for the WASM-mode precedent (overflow only).
	const string OverflowMessage =
		"Error: The custom event 'overflowchange' cannot have the same name as its browserEventName 'overflowchange'. Choose a different name for the custom event.";
	const string AccordionMessage =
		"Error: The event 'accordionchange' is already registered.";
	const string FluentUiModulePath =
		"/_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js:";
	const string FrameworkScriptPathMarker = "/_framework/blazor.web.";

	// Bound to AssertFrameLawAsync's own structural ceiling (preview.91 permits at most 7 live
	// frames -- the outer shell plus PooledIFrame's 5-pool + 1-active canvas ceiling): every
	// distinct top-level document/circuit this run can ever create contributes at most one of each
	// message, so each message type is independently bounded by that same ceiling.
	internal const int MaxRegisteringRuntimes = 7;

	int _acceptedOverflowErrors;
	int _acceptedAccordionErrors;

	internal override bool IsExpectedPageError(string error)
	{
		if (MatchesRegistrationError(error, OverflowMessage, "Overflow"))
		{
			if (_acceptedOverflowErrors >= MaxRegisteringRuntimes)
				return false;
			_acceptedOverflowErrors++;
			return true;
		}
		if (MatchesRegistrationError(error, AccordionMessage, "Accordion"))
		{
			if (_acceptedAccordionErrors >= MaxRegisteringRuntimes)
				return false;
			_acceptedAccordionErrors++;
			return true;
		}
		return false;
	}

	internal static bool MatchesRegistrationError(string error, string expectedMessage, string expectedCallback)
	{
		var lines = error.Split('\n');
		return lines.Length >= 3 &&
			lines[0].StartsWith("page-error page=http://127.0.0.1:", StringComparison.Ordinal) &&
			lines[0].EndsWith($": {expectedMessage}", StringComparison.Ordinal) &&
			lines[1].StartsWith(
				"    at Object.registerCustomEventType (http://127.0.0.1:",
				StringComparison.Ordinal) &&
			lines[1].Contains(FrameworkScriptPathMarker, StringComparison.Ordinal) &&
			lines[1].Contains(".js:", StringComparison.Ordinal) &&
			lines[2].StartsWith("    at Object.", StringComparison.Ordinal) &&
			lines[2].Contains($" [as {expectedCallback}] (http://127.0.0.1:", StringComparison.Ordinal) &&
			lines[2].Contains(FluentUiModulePath, StringComparison.Ordinal);
	}
}

sealed class StoriesRuntimeAudit : IDisposable
{
	readonly IPage _page;
	readonly Uri _origin;
	readonly Func<string> _currentState;
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
		_page.FrameNavigated -= RecordNavigation;
		_page.Console -= RecordConsole;
		_disposed = true;
	}

	void RecordRequest(object? _, IRequest request)
	{
		try
		{
			// Confirmed live (Task 6, 2026-08-22): this host serves "_framework/blazor.web.{hash}.js"
			// (Blazor Web's unified script), never "_framework/blazor.webassembly.*" -- no .wasm
			// runtime exists anywhere on this host. Exactly one such script request fires per
			// top-level document/circuit ever created (the outer shell, and each canvas iframe on
			// its first creation); a pooled canvas that is reused via client-side navigation fires
			// none, matching the old WASM-era bootstrap-counting semantics this replaces.
			if (!TryGetFirstPartyUri(request.Url, out var uri) ||
				!uri.AbsolutePath.StartsWith("/_framework/blazor.web.", StringComparison.Ordinal))
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

	bool TryGetFirstPartyUri(string url, [NotNullWhen(true)] out Uri? uri) =>
		Uri.TryCreate(url, UriKind.Absolute, out uri) &&
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
