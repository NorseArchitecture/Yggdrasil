using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Norse.Hosting.BrowserTesting;

namespace Norse.Hosting.Stories.Server.Tests.BrowserRuntime;

[Collection(StoriesBrowserCollection.Name)]
public sealed class StoriesBrowserRuntimeSmokeTests(StoriesBrowserFixture fixture)
{
	const string StorySelector = ".navigation-tree-item.type-story > .caption a.action";
	const string CatalogScenariosLink = "./?path=/custom/scenarios";
	const string ColdRegisterValidationLink = "./?path=/story/authentication-register--validation-errors";
	const string LoginLockedOutLink = "./?path=/story/authentication-login--locked-out";
	const string LoginNotAllowedLink = "./?path=/story/authentication-login--not-allowed";

	[Fact(Explicit = true, Timeout = 300_000)]
	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "Stories-specific diagnostic append failures are secondary to the primary browser failure.")]
	async Task Released_catalog_renders_every_story_without_recursive_canvas_startup()
	{
		const string TestName = nameof(Released_catalog_renders_every_story_without_recursive_canvas_startup);
		var evidence = await fixture.OpenEvidenceAsync(TestName);
		StoriesRuntimeAudit? audit = null;
		try
		{
			await evidence.ExecuteAsync(async operation =>
			{
				var page = operation.Page;
				audit = new(page, fixture.Origin, () => fixture.CurrentState);
				try
				{
					fixture.CurrentState = "<cold register validation checkpoint>";
					RuntimeCheckpointEvidence coldEvidence = default;
					string coldValidationMessages = "<not observed>";
					await operation.RunPhaseAsync(
						"Cold direct Register validation checkpoint",
						BrowserTimeouts.HostStartup,
						async cancellationToken =>
						{
							await InstallLifecycleProbeAsync(page, cancellationToken);
							var checkpoint = audit.BeginCheckpoint();
							var frame = await OpenDirectStoryAsync(
								page,
								fixture.Origin,
								ColdRegisterValidationLink,
								cancellationToken);
							await AssertRequiredDriverCompleteAsync(page, frame, cancellationToken);
							coldValidationMessages = await AssertColdRegisterValidationAsync(
								page,
								frame,
								cancellationToken);
							await AssertFrameLawAsync(
								page,
								ColdRegisterValidationLink,
								audit,
								cancellationToken);
							coldEvidence = audit.AssertCheckpoint(
								checkpoint,
								"cold Register validation",
								RuntimeCheckpointLaw.ColdStartup);
						});

					fixture.CurrentState = "<catalog scenarios index>";
					await operation.RunPhaseAsync(
						"Stories catalog scenarios navigation",
						BrowserTimeouts.StoryState,
						async cancellationToken =>
						{
							await NavigateCatalogLinkAsync(page, CatalogScenariosLink, cancellationToken);
							await audit.CaptureFrameInventoryAsync(cancellationToken);
						});

					fixture.CurrentState = "<catalog disclosure expansion>";
					await operation.RunPhaseAsync(
						"Stories catalog root disclosure expansion",
						BrowserTimeouts.StoryState,
						cancellationToken => ExpandStoryRootsAsync(page, cancellationToken));

					fixture.CurrentState = "<catalog discovery>";
					DiscoveryResult discovery = default;
					await operation.RunPhaseAsync(
						"Stories catalog rendered-DOM discovery",
						BrowserTimeouts.StoryState,
						async cancellationToken =>
						{
							discovery = await DiscoverLinksAsync(page, cancellationToken);
							await audit.CaptureFrameInventoryAsync(cancellationToken);
						});

					fixture.CurrentState = "<pinned Login Locked Out re-entry checkpoint>";
					RuntimeCheckpointEvidence reentryEvidence = default;
					await operation.RunPhaseAsync(
						"Pinned Login Locked Out re-entry checkpoint",
						BrowserTimeouts.HostStartup,
						async cancellationToken =>
						{
							RequireDiscoveredLink(discovery, LoginLockedOutLink);
							RequireDiscoveredLink(discovery, LoginNotAllowedLink);
							var checkpoint = audit.BeginCheckpoint();
							List<string> checkpointDrivers = [];
							await DriveStoryAsync(
								page,
								LoginLockedOutLink,
								checkpointDrivers,
								audit,
								cancellationToken);
							await AssertStoryMessageAsync(
								page,
								LoginLockedOutLink,
								"This account is locked out. Try again later or reset your password.",
								cancellationToken);
							await DriveStoryAsync(
								page,
								LoginNotAllowedLink,
								checkpointDrivers,
								audit,
								cancellationToken);
							await DriveStoryAsync(
								page,
								LoginLockedOutLink,
								checkpointDrivers,
								audit,
								cancellationToken);
							await AssertStoryMessageAsync(
								page,
								LoginLockedOutLink,
								"This account is locked out. Try again later or reset your password.",
								cancellationToken);
							if (checkpointDrivers.Count != 3)
								throw new BrowserFailure(
									$"Pinned re-entry checkpoint completed {checkpointDrivers.Count} drivers; required 3.");
							reentryEvidence = audit.AssertCheckpoint(
								checkpoint,
								"pinned Login Locked Out re-entry",
								RuntimeCheckpointLaw.PinnedReentry);
						});

					List<string> drivenStates = [];
					var sweepCheckpoint = audit.BeginCheckpoint();
					foreach (var link in discovery.Links)
					{
						fixture.CurrentState = link;
						await operation.RunPhaseAsync(
							$"Stories catalog state {link}",
							BrowserTimeouts.StoryState,
							cancellationToken => DriveStoryAsync(
								page,
								link,
								drivenStates,
								audit,
								cancellationToken));
					}

					if (drivenStates.Count < 7)
						throw new BrowserFailure(
							$"Observed {drivenStates.Count} completed driver-backed states against required floor 7. " +
							$"Observed: {string.Join(", ", drivenStates)}");

					var sweepEvidence = audit.AssertCheckpoint(
						sweepCheckpoint,
						"full dynamic catalog sweep",
						RuntimeCheckpointLaw.FullSweep);
					var redirectCount = audit.AssertRedirectLaw();
					Console.WriteLine(
						$"Cold Register checkpoint: messages={coldValidationMessages}; " +
						$"navigations={coldEvidence.NavigationCount}; bootstraps={coldEvidence.BootstrapCount}; " +
						$"lifecycle={coldEvidence.LifecycleEventCount}; routes={coldEvidence.Navigations}");
					Console.WriteLine(
						$"Pinned Login re-entry checkpoint: navigations={reentryEvidence.NavigationCount}; " +
						$"bootstraps={reentryEvidence.BootstrapCount}; lifecycle={reentryEvidence.LifecycleEventCount}; " +
						$"routes={reentryEvidence.Navigations}");
					Console.WriteLine(
						$"Rendered DOM discovery: selector={StorySelector}; total={discovery.Links.Length}; " +
						$"Authentication={discovery.AuthenticationCount}; Primitives={discovery.PrimitivesCount}; " +
						$"sample={discovery.DomSample}");
					Console.WriteLine(
						$"Catalog sweep: driven={drivenStates.Count}; max-live-frames={audit.MaxLiveFrameCount}; " +
						$"stylesheet-redirects={redirectCount}; bootstrap-requests={audit.BootstrapCount}; " +
						$"checkpoint-navigations={sweepEvidence.NavigationCount}; " +
						$"checkpoint-lifecycle={sweepEvidence.LifecycleEventCount}");
				}
				catch
				{
					await audit.TryCaptureFrameInventoryAsync();
					throw;
				}
				finally
				{
					audit.Dispose();
				}
			}, fixture.CreateEvidencePolicy()).WaitAsync(TestContext.Current.CancellationToken);
		}
		catch (Exception exception)
		{
			if (audit is not null)
			{
				try
				{
					await audit.AppendFailureEvidenceAsync(TestName);
				}
				catch (Exception diagnosticFailure)
				{
					exception.Data["Stories detailed frame evidence"] = diagnosticFailure;
				}
			}
			throw;
		}
	}

	static async Task InstallLifecycleProbeAsync(IPage page, CancellationToken cancellationToken)
	{
		var install = page.AddInitScriptAsync("""
			(() => {
				if (window.location.href === 'about:blank')
					return;
				for (const eventName of ['beforeunload', 'pagehide'])
					window.addEventListener(eventName, () =>
						console.info(`[NORSE-STORY-LIFECYCLE] frame=${window === window.top ? 'top' : 'child'} event=${eventName} url=${window.location.href}`));
			})();
			""");
		await AwaitPlaywrightActionAsync(page, install, cancellationToken);
	}

	static async Task<IFrame> OpenDirectStoryAsync(
		IPage page,
		Uri origin,
		string link,
		CancellationToken cancellationToken)
	{
		var expectedUrl = new Uri(origin, link);
		await FrameworkRequestQuiescence.WaitAsync(
			page,
			origin,
			expectedUrl.AbsoluteUri,
			cancellationToken);
		await AwaitPlaywrightActionAsync(
			page,
			page.WaitForURLAsync(
				url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
					uri.PathAndQuery == expectedUrl.PathAndQuery,
				new() { Timeout = BrowserTimeouts.PlaywrightHostStartupMilliseconds }),
			cancellationToken);
		return await AwaitStoryFrameAsync(page, link, cancellationToken);
	}

	static async Task NavigateCatalogLinkAsync(
		IPage page,
		string link,
		CancellationToken cancellationToken)
	{
		var target = page.Locator($"a[href=\"{EscapeCssAttribute(link)}\"]").First;
		await AwaitPlaywrightActionAsync(
			page,
			Assertions.Expect(target).ToBeVisibleAsync(
				new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
			cancellationToken);
		var expectedUrl = new Uri(new Uri(page.Url), link);
		await AwaitPlaywrightActionAsync(
			page,
			target.ClickAsync(new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
			cancellationToken);
		await AwaitPlaywrightActionAsync(
			page,
			page.WaitForURLAsync(
				url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
					uri.PathAndQuery == expectedUrl.PathAndQuery,
				new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
			cancellationToken);
	}

	static async Task AssertRequiredDriverCompleteAsync(
		IPage page,
		IFrame frame,
		CancellationToken cancellationToken)
	{
		var driver = frame.Locator("[data-norse-story-driver-state]");
		await AwaitPlaywrightActionAsync(
			page,
			Assertions.Expect(driver).ToHaveCountAsync(
				1,
				new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
			cancellationToken);
		await AwaitPlaywrightActionAsync(
			page,
			Assertions.Expect(driver).ToHaveAttributeAsync(
				"data-norse-story-driver-state",
				"complete",
				new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
			cancellationToken);
	}

	static async Task<string> AssertColdRegisterValidationAsync(
		IPage page,
		IFrame frame,
		CancellationToken cancellationToken)
	{
		var form = frame.Locator("form");
		string[] expectedMessages =
		[
			"'Password' must not be empty.",
			"The length of 'Password' must be at least 8 characters.",
		];
		foreach (var message in expectedMessages)
			await AwaitPlaywrightActionAsync(
				page,
				Assertions.Expect(form).ToContainTextAsync(
					message,
					new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
				cancellationToken);

		return await AwaitPlaywrightResultAsync(
			page,
			form.InnerTextAsync(new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
			cancellationToken);
	}

	static void RequireDiscoveredLink(DiscoveryResult discovery, string requiredLink)
	{
		if (!discovery.Links.Contains(requiredLink, StringComparer.Ordinal))
			throw new BrowserFailure(
				$"Pinned checkpoint requires rendered link {requiredLink}. " +
				$"Observed: {string.Join(", ", discovery.Links)}");
	}

	static async Task AssertStoryMessageAsync(
		IPage page,
		string link,
		string expectedMessage,
		CancellationToken cancellationToken)
	{
		var frame = await AwaitStoryFrameAsync(page, link, cancellationToken);
		await AwaitPlaywrightActionAsync(
			page,
			Assertions.Expect(frame.Locator(".norse-model-errors")).ToContainTextAsync(
				expectedMessage,
				new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
			cancellationToken);
	}

	static async Task<DiscoveryResult> DiscoverLinksAsync(
		IPage page,
		CancellationToken cancellationToken)
	{
		var stories = page.Locator(StorySelector);
		var links = await AwaitPlaywrightResultAsync(
			page,
			stories.EvaluateAllAsync<string[]>(
				"anchors => anchors.map(anchor => anchor.getAttribute('href')).filter(href => href !== null)"),
			cancellationToken);

		if (links.Length == 0)
		{
			var navigation = page.Locator(".navigation-tree");
			var navigationCount = await AwaitPlaywrightResultAsync(
				page,
				navigation.CountAsync(),
				cancellationToken);
			var renderedSubtree = navigationCount > 0 ?
				await AwaitPlaywrightResultAsync(
					page,
					navigation.First.EvaluateAsync<string>("element => element.outerHTML"),
					cancellationToken) :
				await AwaitPlaywrightResultAsync(
					page,
					page.Locator("body").EvaluateAsync<string>("element => element.innerHTML"),
					cancellationToken);
			throw new BrowserFailure(
				$"Rendered story discovery returned 0 states for selector {StorySelector}. " +
				$"Rendered navigation subtree: {renderedSubtree}");
		}

		var authenticationCount = links.Count(static path =>
			path.Contains("/story/authentication-", StringComparison.Ordinal));
		var primitivesCount = links.Count(static path =>
			path.Contains("/story/primitives-", StringComparison.Ordinal));
		if (links.Length < 20)
			throw new BrowserFailure(
				$"Rendered story discovery returned {links.Length} states against required floor 20. " +
				$"Observed href values: {string.Join(", ", links)}");
		if (authenticationCount == 0 || primitivesCount == 0)
			throw new BrowserFailure(
				$"Rendered story roots were incomplete: Authentication={authenticationCount}, " +
				$"Primitives={primitivesCount}. Observed href values: {string.Join(", ", links)}");

		var domSample = await AwaitPlaywrightResultAsync(
			page,
			stories.First.EvaluateAsync<string>(
				"anchor => anchor.closest('.navigation-tree-item')?.outerHTML ?? anchor.outerHTML"),
			cancellationToken);
		return new(links, authenticationCount, primitivesCount, domSample);
	}

	static async Task DriveStoryAsync(
		IPage page,
		string link,
		List<string> drivenStates,
		StoriesRuntimeAudit audit,
		CancellationToken cancellationToken)
	{
		var target = page.Locator($"a[href=\"{EscapeCssAttribute(link)}\"]");
		await ExpandDisclosuresUntilVisibleAsync(page, target, link, cancellationToken);
		await AwaitPlaywrightActionAsync(
			page,
			Assertions.Expect(target).ToBeVisibleAsync(
				new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
			cancellationToken);
		var expectedUrl = new Uri(new Uri(page.Url), link);
		await AwaitPlaywrightActionAsync(
			page,
			target.ClickAsync(new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
			cancellationToken);
		await AwaitPlaywrightActionAsync(
			page,
			page.WaitForURLAsync(
				url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
					uri.PathAndQuery == expectedUrl.PathAndQuery &&
					uri.Fragment == expectedUrl.Fragment,
				new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
			cancellationToken);

		await audit.CaptureFrameInventoryAsync(cancellationToken);
		var frame = await AwaitStoryFrameAsync(page, link, cancellationToken);

		var driver = frame.Locator("[data-norse-story-driver-state]");
		var driverCount = await AwaitPlaywrightResultAsync(
			page,
			driver.CountAsync(),
			cancellationToken);
		if (driverCount > 0)
		{
			if (driverCount != 1)
				throw new BrowserFailure(
					$"State {link} rendered {driverCount} story-driver markers; required exactly 1.");
			drivenStates.Add(link);
			await AwaitPlaywrightActionAsync(
				page,
				Assertions.Expect(driver).ToHaveAttributeAsync(
					"data-norse-story-driver-state",
					"complete",
					new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
				cancellationToken);
		}

		await AssertFrameLawAsync(page, link, audit, cancellationToken);
	}

	static async Task<IFrame> AwaitStoryFrameAsync(
		IPage page,
		string link,
		CancellationToken cancellationToken)
	{
		var expectedUrl = new Uri(new Uri(page.Url), link);
		var storyId = StoryIdFrom(expectedUrl, link);
		var active = page.Locator(".canvas-container iframe");
		await AwaitPlaywrightActionAsync(
			page,
			Assertions.Expect(active).ToHaveCountAsync(
				1,
				new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
			cancellationToken);
		var handle = await AwaitPlaywrightResultAsync(
			page,
			active.ElementHandleAsync(),
			cancellationToken) ?? throw new BrowserFailure($"No active iframe for {link}.");
		var frame = await AwaitPlaywrightResultAsync(
			page,
			handle.ContentFrameAsync(),
			cancellationToken) ?? throw new BrowserFailure($"No active frame for {link}.");
		await AwaitPlaywrightActionAsync(
			page,
			frame.WaitForURLAsync(
				url => IsStoryCanvasUrl(url, storyId),
				new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
			cancellationToken);
		await AwaitPlaywrightActionAsync(
			page,
			Assertions.Expect(frame.Locator("body")).ToHaveAttributeAsync(
				"data-bs-parent-frame",
				"story",
				new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
			cancellationToken);
		await AwaitPlaywrightActionAsync(
			page,
			Assertions.Expect(frame.Locator("#app > *")).Not.ToHaveCountAsync(
				0,
				new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
			cancellationToken);
		return frame;
	}

	static async Task ExpandDisclosuresUntilVisibleAsync(
		IPage page,
		ILocator target,
		string link,
		CancellationToken cancellationToken)
	{
		// IsVisibleAsync is an immediate probe; Playwright 1.61 marks its Timeout option obsolete
		// and ignored. The surrounding 15-second phase token remains the terminal budget.
		if (await AwaitPlaywrightResultAsync(
			page,
			target.IsVisibleAsync(),
			cancellationToken))
			return;

		throw new BrowserFailure(
			$"Rendered story link {link} remained hidden after the audited root expand-all phase.");
	}

	static async Task ExpandStoryRootsAsync(IPage page, CancellationToken cancellationToken)
	{
		var expandAll = page.Locator(
			$".navigation-tree-item.type-container:has({StorySelector}) > .caption " +
			"button.sub-heading-action:has(use[href='#icon--expandall']):visible");
		while (await AwaitPlaywrightResultAsync(
			page,
			expandAll.CountAsync(),
			cancellationToken) > 0)
		{
			var disclosure = expandAll.First;
			await AwaitPlaywrightActionAsync(
				page,
				Assertions.Expect(disclosure).ToBeVisibleAsync(
					new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
				cancellationToken);
			await AwaitPlaywrightActionAsync(
				page,
				disclosure.ClickAsync(new()
				{
					Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds,
				}),
				cancellationToken);
		}
	}

	static async Task AssertFrameLawAsync(
		IPage page,
		string link,
		StoriesRuntimeAudit audit,
		CancellationToken cancellationToken)
	{
		await audit.CaptureFrameInventoryAsync(cancellationToken);
		var frames = page.Frames.ToArray();
		if (frames.Length > 7)
			throw new BrowserFailure(
				$"State {link} exposed {frames.Length} live frames; preview.91 permits at most 7.");

		var canvases = frames.Where(frame => !ReferenceEquals(frame, page.MainFrame)).ToArray();
		if (canvases.Length == 0)
			throw new BrowserFailure($"State {link} exposed no live canvas frames.");

		foreach (var frame in canvases)
		{
			if (!Uri.TryCreate(frame.Url, UriKind.Absolute, out var frameUri) ||
				frameUri.AbsolutePath != "/iframe.html")
				throw new BrowserFailure(
					$"State {link} exposed canvas URL {frame.Url}; required absolute path /iframe.html.");

			var body = frame.Locator("body");
			await AwaitPlaywrightActionAsync(
				page,
				Assertions.Expect(body).ToHaveAttributeAsync(
					"data-bs-parent-frame",
					new Regex("^(story|docs)$"),
					new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
				cancellationToken);
			var marker = await AwaitPlaywrightResultAsync(
				page,
				body.GetAttributeAsync(
					"data-bs-parent-frame",
					new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
				cancellationToken);
			if (marker is not ("story" or "docs"))
				throw new BrowserFailure(
					$"State {link} exposed canvas marker {marker ?? "<none>"}; required story or docs.");
			if (frame.ChildFrames.Count != 0)
				throw new BrowserFailure(
					$"State {link} exposed {frame.ChildFrames.Count} descendant frame(s) beneath {frame.Url}.");

			await AwaitPlaywrightActionAsync(
				page,
				Assertions.Expect(frame.Locator(".navigation-tree, .sidebar .explorer-menu")).ToHaveCountAsync(
					0,
					new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
				cancellationToken);
			await AwaitPlaywrightActionAsync(
				page,
				Assertions.Expect(frame.Locator("#blazor-error-ui")).ToBeHiddenAsync(
					new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
				cancellationToken);
			await AwaitPlaywrightActionAsync(
				page,
				Assertions.Expect(frame.Locator("#blazor-error-ui .reload")).ToBeHiddenAsync(
					new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
				cancellationToken);
			await AwaitPlaywrightActionAsync(
				page,
				Assertions.Expect(frame.Locator(".loading-progress")).ToBeHiddenAsync(
					new() { Timeout = BrowserTimeouts.PlaywrightStoryStateMilliseconds }),
				cancellationToken);
		}
	}

	static string EscapeCssAttribute(string value) =>
		value.Replace("\\", "\\\\", StringComparison.Ordinal)
			.Replace("\"", "\\\"", StringComparison.Ordinal);

	static string StoryIdFrom(Uri expectedUrl, string link)
	{
		const string StoryPathPrefix = "?path=/story/";
		var decodedQuery = Uri.UnescapeDataString(expectedUrl.Query);
		if (!decodedQuery.StartsWith(StoryPathPrefix, StringComparison.Ordinal) ||
			decodedQuery.Length == StoryPathPrefix.Length)
			throw new BrowserFailure($"Rendered story href {link} did not expose the required /story/{{id}} path.");

		return decodedQuery[StoryPathPrefix.Length..];
	}

	static bool IsStoryCanvasUrl(string url, string storyId)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/iframe.html")
			return false;

		return uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
			.Any(part => part.Equals($"id={storyId}", StringComparison.Ordinal));
	}

	static async Task<T> AwaitPlaywrightResultAsync<T>(
		IPage page,
		Task<T> action,
		CancellationToken cancellationToken)
	{
		await AwaitPlaywrightActionAsync(page, action, cancellationToken);
		return await action;
	}

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "Page-close and action failures are diagnostics on the primary cancellation.")]
	static async Task AwaitPlaywrightActionAsync(
		IPage page,
		Task action,
		CancellationToken cancellationToken)
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
				await page.CloseAsync(new() { RunBeforeUnload = false });
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
				exception.Data["Stories Playwright action cancellation cleanup"] = new AggregateException(
					"Stories Playwright action cancellation cleanup failed.",
					cleanupFailures);
			throw;
		}
	}

	readonly record struct DiscoveryResult(
		string[] Links,
		int AuthenticationCount,
		int PrimitivesCount,
		string DomSample);
}

public sealed class StoriesBrowserEvidencePolicyTests
{
	static readonly Uri _origin = new("http://127.0.0.1:54321");
	const string OverflowError = """
		page-error page=http://127.0.0.1:54321/?path=/story/authentication-login--default: Error: The custom event 'overflowchange' cannot have the same name as its browserEventName 'overflowchange'. Choose a different name for the custom event.
		    at Object.registerCustomEventType (http://127.0.0.1:54321/_framework/blazor.webassembly.ax9cgflnhi.js:1:60603)
		    at Object.d [as Overflow] (http://127.0.0.1:54321/_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js:6004:5984)
		    at A (http://127.0.0.1:54321/_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js:6004:9689)
		""";
	const string AccordionError = """
		page-error page=http://127.0.0.1:54321/: Error: The event 'accordionchange' is already registered.
		    at Object.registerCustomEventType (http://127.0.0.1:54321/_framework/blazor.webassembly.ax9cgflnhi.js:1:60603)
		    at Object.s [as Accordion] (http://127.0.0.1:54321/_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js:6004:3548)
		""";

	[Fact]
	void Fluent_UI_overflow_allowance_is_exact_and_bounded_to_seven_runtimes()
	{
		StoriesBrowserEvidencePolicy policy = new(_origin);

		for (var runtime = 0; runtime < 7; runtime++)
			policy.IsExpectedPageError(OverflowError).ShouldBeTrue();
		policy.IsExpectedPageError(OverflowError).ShouldBeFalse();
	}

	[Fact]
	void Fluent_UI_overflow_allowance_rejects_message_stack_alias_and_origin_near_misses()
	{
		StoriesBrowserEvidencePolicy policy = new(_origin);

		policy.IsExpectedPageError(AccordionError).ShouldBeFalse();
		policy.IsExpectedPageError(
			OverflowError.Replace("Choose a different name", "Please choose a different name")).ShouldBeFalse();
		policy.IsExpectedPageError(
			OverflowError.Replace("[as Overflow]", "[as Accordion]")).ShouldBeFalse();
		policy.IsExpectedPageError(
			OverflowError.Replace("http://127.0.0.1:54321/_framework", "https://cdn.example.test/_framework"))
			.ShouldBeFalse();
		policy.IsExpectedPageError(
			OverflowError.Replace(
				"Microsoft.FluentUI.AspNetCore.Components.lib.module.js",
				"other.lib.module.js")).ShouldBeFalse();
	}

	[Fact]
	void Scoped_stylesheet_redirect_policy_accepts_only_the_exact_302_chain_start()
	{
		StoriesBrowserEvidencePolicy policy = new(_origin);

		policy.IsExpectedRedirect(Response(
			302,
			"GET",
			StoriesBrowserEvidencePolicy.RequestedStylesheetPath,
			null)).ShouldBeTrue();
		policy.IsExpectedRedirect(Response(
			302,
			"GET",
			StoriesBrowserEvidencePolicy.RequestedStylesheetPath,
			StoriesBrowserEvidencePolicy.TargetStylesheetPath)).ShouldBeTrue();
		policy.IsExpectedRedirect(Response(
			301,
			"GET",
			StoriesBrowserEvidencePolicy.RequestedStylesheetPath,
			StoriesBrowserEvidencePolicy.TargetStylesheetPath)).ShouldBeFalse();
		policy.IsExpectedRedirect(Response(
			302,
			"POST",
			StoriesBrowserEvidencePolicy.RequestedStylesheetPath,
			StoriesBrowserEvidencePolicy.TargetStylesheetPath)).ShouldBeFalse();
	}

	[Fact]
	void Scoped_stylesheet_redirect_policy_rejects_source_and_origin_near_misses()
	{
		StoriesBrowserEvidencePolicy policy = new(_origin);

		policy.IsExpectedRedirect(Response(
			302,
			"GET",
			$"{StoriesBrowserEvidencePolicy.RequestedStylesheetPath}?v=1",
			StoriesBrowserEvidencePolicy.TargetStylesheetPath)).ShouldBeFalse();
		policy.IsExpectedRedirect(Response(
			302,
			"GET",
			StoriesBrowserEvidencePolicy.RequestedStylesheetPath,
			StoriesBrowserEvidencePolicy.TargetStylesheetPath,
			sourceOrigin: "http://localhost:54321")).ShouldBeFalse();
	}

	[Fact]
	void Scoped_stylesheet_redirect_terminal_requires_the_exact_correlated_200_target()
	{
		var source = Response(
			302,
			"GET",
			StoriesBrowserEvidencePolicy.RequestedStylesheetPath,
			null);

		StoriesBrowserEvidencePolicy.MatchesExpectedRedirectTarget(
			TargetResponse(200, StoriesBrowserEvidencePolicy.TargetStylesheetPath, source.Request),
			source.Request,
			_origin).ShouldBeTrue();
		StoriesBrowserEvidencePolicy.MatchesExpectedRedirectTarget(
			TargetResponse(204, StoriesBrowserEvidencePolicy.TargetStylesheetPath, source.Request),
			source.Request,
			_origin).ShouldBeFalse();
		StoriesBrowserEvidencePolicy.MatchesExpectedRedirectTarget(
			TargetResponse(200, "/Norse.Hosting.Stories.Client.other.css", source.Request),
			source.Request,
			_origin).ShouldBeFalse();
		StoriesBrowserEvidencePolicy.MatchesExpectedRedirectTarget(
			TargetResponse(200, StoriesBrowserEvidencePolicy.TargetStylesheetPath, Substitute.For<IRequest>()),
			source.Request,
			_origin).ShouldBeFalse();
	}

	[Fact]
	void Cold_checkpoint_requires_exactly_one_outer_and_one_canvas_bootstrap()
	{
		RuntimeCheckpointPolicy.Assert(
			"cold",
			RuntimeCheckpointLaw.ColdStartup,
			[OuterBootstrap(), CanvasBootstrap()],
			[]);

		Should.Throw<BrowserFailure>(() => RuntimeCheckpointPolicy.Assert(
			"cold",
			RuntimeCheckpointLaw.ColdStartup,
			[OuterBootstrap()],
			[]));
		Should.Throw<BrowserFailure>(() => RuntimeCheckpointPolicy.Assert(
			"cold",
			RuntimeCheckpointLaw.ColdStartup,
			[OuterBootstrap(), CanvasBootstrap(), CanvasBootstrap()],
			[]));
		Should.Throw<BrowserFailure>(() => RuntimeCheckpointPolicy.Assert(
			"cold",
			RuntimeCheckpointLaw.ColdStartup,
			[OuterBootstrap(), NestedBootstrap()],
			[]));
	}

	[Fact]
	void Cold_checkpoint_requires_zero_lifecycle_events()
	{
		var bootstraps = new[] { OuterBootstrap(), CanvasBootstrap() };
		var initialPagehide = new RuntimeLifecycleObservation(
			"cold",
			"pagehide",
			"about:blank",
			true);

		RuntimeCheckpointPolicy.Assert(
			"cold",
			RuntimeCheckpointLaw.ColdStartup,
			bootstraps,
			[]);
		Should.Throw<BrowserFailure>(() => RuntimeCheckpointPolicy.Assert(
			"cold",
			RuntimeCheckpointLaw.ColdStartup,
			bootstraps,
			[initialPagehide]));
		Should.Throw<BrowserFailure>(() => RuntimeCheckpointPolicy.Assert(
			"cold",
			RuntimeCheckpointLaw.ColdStartup,
			bootstraps,
			[initialPagehide with { IsTopFrame = false }]));
	}

	[Fact]
	void Lifecycle_probe_payload_preserves_top_and_child_frame_provenance()
	{
		RuntimeLifecycleObservation.TryParse(
			"cold",
			"[NORSE-STORY-LIFECYCLE] frame=top event=pagehide url=about:blank",
			out var top).ShouldBeTrue();
		top.ShouldNotBeNull();
		top.IsTopFrame.ShouldBeTrue();
		top.EventName.ShouldBe("pagehide");
		top.Url.ShouldBe("about:blank");

		RuntimeLifecycleObservation.TryParse(
			"cold",
			"[NORSE-STORY-LIFECYCLE] frame=child event=beforeunload url=http://127.0.0.1:54321/iframe.html",
			out var child).ShouldBeTrue();
		child.ShouldNotBeNull();
		child.IsTopFrame.ShouldBeFalse();
		RuntimeLifecycleObservation.TryParse(
			"cold",
			"[NORSE-STORY-LIFECYCLE] event=pagehide url=about:blank",
			out _).ShouldBeFalse();
	}

	[Fact]
	void Pinned_reentry_requires_zero_bootstraps_and_zero_lifecycle_events()
	{
		RuntimeCheckpointPolicy.Assert(
			"re-entry",
			RuntimeCheckpointLaw.PinnedReentry,
			[],
			[]);

		Should.Throw<BrowserFailure>(() => RuntimeCheckpointPolicy.Assert(
			"re-entry",
			RuntimeCheckpointLaw.PinnedReentry,
			[CanvasBootstrap()],
			[]));
		Should.Throw<BrowserFailure>(() => RuntimeCheckpointPolicy.Assert(
			"re-entry",
			RuntimeCheckpointLaw.PinnedReentry,
			[],
			[new("re-entry", "pagehide", "about:blank", true)]));
	}

	[Fact]
	void Full_sweep_allows_legitimate_canvas_bootstraps_but_no_lifecycle_or_nested_catalog()
	{
		RuntimeCheckpointPolicy.Assert(
			"sweep",
			RuntimeCheckpointLaw.FullSweep,
			[CanvasBootstrap()],
			[]);

		Should.Throw<BrowserFailure>(() => RuntimeCheckpointPolicy.Assert(
			"sweep",
			RuntimeCheckpointLaw.FullSweep,
			[NestedBootstrap()],
			[]));
		Should.Throw<BrowserFailure>(() => RuntimeCheckpointPolicy.Assert(
			"sweep",
			RuntimeCheckpointLaw.FullSweep,
			[],
			[new("sweep", "pagehide", "about:blank", true)]));
	}

	static RuntimeBootstrapObservation OuterBootstrap() =>
		new("cold", "http://127.0.0.1:54321/_framework/blazor.webassembly.js", "http://127.0.0.1:54321/", true);

	static RuntimeBootstrapObservation CanvasBootstrap() =>
		new("story", "http://127.0.0.1:54321/_framework/blazor.webassembly.js", "http://127.0.0.1:54321/iframe.html?id=story", false);

	static RuntimeBootstrapObservation NestedBootstrap() =>
		new("story", "http://127.0.0.1:54321/_framework/blazor.webassembly.js", "http://127.0.0.1:54321/", false);

	static IResponse Response(
		int status,
		string method,
		string sourcePath,
		string? targetPath,
		string? targetOrigin = null,
		string? sourceOrigin = null)
	{
		var request = Substitute.For<IRequest>();
		request.Method.Returns(method);
		if (targetPath is not null)
		{
			var redirectedTo = Substitute.For<IRequest>();
			redirectedTo.Url.Returns($"{targetOrigin ?? _origin.GetLeftPart(UriPartial.Authority)}{targetPath}");
			request.RedirectedTo.Returns(redirectedTo);
		}
		var response = Substitute.For<IResponse>();
		response.Status.Returns(status);
		var sourceBase = new Uri(sourceOrigin ?? _origin.GetLeftPart(UriPartial.Authority) + "/");
		response.Url.Returns(new Uri(sourceBase, sourcePath).AbsoluteUri);
		response.Request.Returns(request);
		return response;
	}

	static IResponse TargetResponse(int status, string targetPath, IRequest redirectedFrom)
	{
		var request = Substitute.For<IRequest>();
		request.Method.Returns("GET");
		request.RedirectedFrom.Returns(redirectedFrom);
		var response = Substitute.For<IResponse>();
		response.Status.Returns(status);
		response.Url.Returns(new Uri(_origin, targetPath).AbsoluteUri);
		response.Request.Returns(request);
		return response;
	}
}
