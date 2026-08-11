using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Norse.Hosting.BrowserTesting;

namespace Norse.Hosting.Web.Server.Tests.BrowserRuntime;

[Collection(WebServerBrowserCollection.Name)]
public sealed class WebServerBrowserRuntimeSmokeTests(WebServerBrowserFixture fixture)
{
	[Fact(Explicit = true, Timeout = 300_000)]
	async Task Interactive_auto_executes_a_successful_country_lookup_in_webassembly()
	{
		var evidence = await fixture.OpenEvidenceAsync(
			nameof(Interactive_auto_executes_a_successful_country_lookup_in_webassembly));
		await evidence.ExecuteAsync(async operation =>
		{
			var page = operation.Page;
			await operation.RunPhaseAsync(
				"InteractiveAuto framework warm-up",
				BrowserTimeouts.HostStartup,
				cancellationToken => FrameworkRequestQuiescence.WaitAsync(
					page,
					fixture.Origin,
					cancellationToken));

			await operation.RunPhaseAsync(
				"InteractiveAuto WebAssembly readiness",
				BrowserTimeouts.HostStartup,
				async cancellationToken =>
				{
					await AwaitPlaywrightActionAsync(page, page.GotoAsync("/reference/country-lookup", new()
					{
						Timeout = BrowserTimeouts.PlaywrightHostStartupMilliseconds,
					}), cancellationToken);

					var marker = page.Locator("[data-norse-renderer]");
					await AwaitPlaywrightActionAsync(page, Assertions.Expect(marker).ToHaveAttributeAsync(
						"data-norse-renderer",
						"WebAssembly",
						new() { Timeout = BrowserTimeouts.PlaywrightHostStartupMilliseconds }), cancellationToken);
					await AwaitPlaywrightActionAsync(page, Assertions.Expect(marker).ToHaveAttributeAsync(
						"data-norse-interactive",
						"true",
						new() { Timeout = BrowserTimeouts.PlaywrightHostStartupMilliseconds }), cancellationToken);
					TestContext.Current.TestOutputHelper?.WriteLine(
						"Renderer marker observed: data-norse-renderer=WebAssembly, data-norse-interactive=true");
				});

			await operation.RunPhaseAsync(
				"gRPC-Web dispatch/render",
				BrowserTimeouts.BrowserOperation,
				async cancellationToken =>
				{
					await DispatchAndObserveCountryLookupAsync(page, cancellationToken);

					await ExpectCountryDetailAsync(page, "Alpha-2", "US", cancellationToken);
					await ExpectCountryDetailAsync(page, "Alpha-3", "USA", cancellationToken);
					await ExpectCountryDetailAsync(
						page,
						"Name",
						"United States of America",
						cancellationToken);
					await AwaitPlaywrightActionAsync(
						page,
						Assertions.Expect(page.GetByText("Match", new() { Exact = true })).ToBeVisibleAsync(
							new() { Timeout = BrowserTimeouts.PlaywrightOperationMilliseconds }),
						cancellationToken);
				});
		}, WebServerBrowserFixture.CreateEvidencePolicy());
	}

	static async Task DispatchAndObserveCountryLookupAsync(
		IPage page,
		CancellationToken cancellationToken)
	{
		const string CountryLookupPath = "/grpc.reference.v1.ReferenceService/GetCountry";
		TaskCompletionSource<IRequest> requestCompletion =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<IResponse> responseCompletion =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		void Requested(object? _, IRequest request)
		{
			if (IsCountryLookupRequest(request))
				requestCompletion.TrySetResult(request);
		}

		void Responded(object? _, IResponse response)
		{
			if (requestCompletion.Task.IsCompletedSuccessfully &&
				ReferenceEquals(response.Request, requestCompletion.Task.Result))
				responseCompletion.TrySetResult(response);
		}

		page.Request += Requested;
		page.Response += Responded;
		using var cancellationRegistration = cancellationToken.Register(() =>
		{
			requestCompletion.TrySetCanceled(cancellationToken);
			responseCompletion.TrySetCanceled(cancellationToken);
		});

		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			await AwaitPlaywrightActionAsync(
				page,
				page.GetByLabel("Code").Locator("input").FillAsync("US", new()
				{
					Timeout = BrowserTimeouts.PlaywrightOperationMilliseconds,
				}),
				cancellationToken);

			var lookup = page.GetByText("Look up", new() { Exact = true });
			await AwaitPlaywrightActionAsync(
				page,
				Assertions.Expect(lookup).ToBeVisibleAsync(new()
				{
					Timeout = BrowserTimeouts.PlaywrightOperationMilliseconds,
				}),
				cancellationToken);
			await AwaitPlaywrightActionAsync(
				page,
				lookup.ClickAsync(new()
				{
					Timeout = BrowserTimeouts.PlaywrightOperationMilliseconds,
				}),
				cancellationToken);

			var request = await requestCompletion.Task;
			var contentType = request.Headers["content-type"];
			contentType.ShouldStartWith("application/grpc-web+proto");
			TestContext.Current.TestOutputHelper?.WriteLine(
				$"gRPC-Web request observed: {request.Method} {new Uri(request.Url).AbsolutePath}; " +
				$"content-type={contentType}");

			var response = await responseCompletion.Task;
			response.Ok.ShouldBeTrue();
			TestContext.Current.TestOutputHelper?.WriteLine(
				$"gRPC-Web response observed: status={response.Status}");
		}
		finally
		{
			page.Request -= Requested;
			page.Response -= Responded;
		}

		bool IsCountryLookupRequest(IRequest request) =>
			request.Method == "POST" &&
			Uri.TryCreate(request.Url, UriKind.Absolute, out var requestUri) &&
			requestUri.AbsolutePath == CountryLookupPath;
	}

	static async Task ExpectCountryDetailAsync(
		IPage page,
		string label,
		string value,
		CancellationToken cancellationToken)
	{
		var exactLabel = new Regex($"^{Regex.Escape(label)}$");
		var term = page.Locator("dt").Filter(new() { HasTextRegex = exactLabel });
		var definition = term.Locator("xpath=following-sibling::*[1][self::dd]");
		await AwaitPlaywrightActionAsync(
			page,
			Assertions.Expect(definition).ToHaveTextAsync(
				value,
				new() { Timeout = BrowserTimeouts.PlaywrightOperationMilliseconds }),
			cancellationToken);
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
				exception.Data["Playwright action cancellation cleanup"] = new AggregateException(
					"Playwright action cancellation cleanup failed.",
					cleanupFailures);
			throw;
		}
	}
}

public sealed class WebServerBrowserEvidencePolicyTests
{
	const string OverflowError = """
		page-error page=http://127.0.0.1:54321/: Error: The custom event 'overflowchange' cannot have the same name as its browserEventName 'overflowchange'. Choose a different name for the custom event.
		    at Object.registerCustomEventType (http://127.0.0.1:54321/_framework/blazor.web.tz6by93kvf.js:1:63207)
		    at Object.d [as Overflow] (http://127.0.0.1:54321/_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js:6004:5984)
		""";
	const string AccordionError = """
		page-error page=http://127.0.0.1:54321/: Error: The event 'accordionchange' is already registered.
		    at Object.registerCustomEventType (http://127.0.0.1:54321/_framework/blazor.web.tz6by93kvf.js:1:63102)
		    at Object.s [as Accordion] (http://127.0.0.1:54321/_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js:6004:3548)
		""";

	[Fact]
	void Fluent_UI_rc5_startup_allowances_are_bounded_to_two_of_each_exact_signature()
	{
		WebServerBrowserEvidencePolicy policy = new();

		policy.IsExpectedPageError(OverflowError).ShouldBeTrue();
		policy.IsExpectedPageError(OverflowError).ShouldBeTrue();
		policy.IsExpectedPageError(OverflowError).ShouldBeFalse();
		policy.IsExpectedPageError(AccordionError).ShouldBeTrue();
		policy.IsExpectedPageError(AccordionError).ShouldBeTrue();
		policy.IsExpectedPageError(AccordionError).ShouldBeFalse();
	}

	[Fact]
	void Fluent_UI_rc5_startup_allowances_reject_message_and_stack_near_misses()
	{
		WebServerBrowserEvidencePolicy policy = new();
		var wrongMessage = OverflowError.Replace("overflowchange'. Choose", "overflowchange'. Please choose");
		var externalStack = AccordionError.Replace("http://127.0.0.1:54321", "https://cdn.example.test");
		var wrongAlias = OverflowError.Replace("[as Overflow]", "[as Accordion]");

		policy.IsExpectedPageError(wrongMessage).ShouldBeFalse();
		policy.IsExpectedPageError(externalStack).ShouldBeFalse();
		policy.IsExpectedPageError(wrongAlias).ShouldBeFalse();
	}

	[Fact]
	void InteractiveServer_disconnect_allowance_is_exact_and_single_use()
	{
		WebServerBrowserEvidencePolicy policy = new();
		var expected = Request("POST", "/_blazor/disconnect", "net::ERR_ABORTED");

		policy.IsExpectedRequestFailure(expected).ShouldBeTrue();
		policy.IsExpectedRequestFailure(expected).ShouldBeFalse();
		new WebServerBrowserEvidencePolicy()
			.IsExpectedRequestFailure(Request("GET", "/_blazor/disconnect", "net::ERR_ABORTED"))
			.ShouldBeFalse();
		new WebServerBrowserEvidencePolicy()
			.IsExpectedRequestFailure(Request("POST", "/_blazor/negotiate", "net::ERR_ABORTED"))
			.ShouldBeFalse();
		new WebServerBrowserEvidencePolicy()
			.IsExpectedRequestFailure(Request("POST", "/_blazor/disconnect", "net::ERR_FAILED"))
			.ShouldBeFalse();

		static IRequest Request(string method, string path, string failure)
		{
			var request = Substitute.For<IRequest>();
			request.Method.Returns(method);
			request.Url.Returns($"http://127.0.0.1:54321{path}");
			request.Failure.Returns(failure);
			return request;
		}
	}
}
