using System.Reflection;
using Microsoft.Playwright;
using Norse.Hosting.BrowserTesting;

namespace Norse.Hosting.Stories.Server.Tests;

public sealed class BrowserTimeoutClassificationTests
{
	[Fact]
	async Task Phase_budget_expiry_is_classified_as_the_named_phase()
	{
		BrowserPhaseRunner runner = new(CancellationToken.None);

		var exception = await Should.ThrowAsync<BrowserFailure>(() => runner.RunAsync(
			"framework warm-up",
			TimeSpan.FromMilliseconds(50),
			cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)));

		exception.Message.ShouldContain("Browser phase 'framework warm-up' timed out");
		exception.Message.ShouldNotContain("Aggregate browser-test ceiling expired");
		runner.TimedOutPhase.ShouldBe("framework warm-up");
	}

	[Fact]
	async Task Aggregate_expiry_during_a_phase_does_not_accuse_the_phase_budget()
	{
		using var aggregate = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
		BrowserPhaseRunner runner = new(aggregate.Token);

		var exception = await Should.ThrowAsync<BrowserFailure>(() => runner.RunAsync(
			"framework warm-up",
			TimeSpan.FromSeconds(5),
			cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)));

		exception.Message.ShouldContain("Aggregate browser-test ceiling expired during phase 'framework warm-up'");
		exception.Message.ShouldContain("phase budget 5.0s was not exceeded");
		runner.TimedOutPhase.ShouldBeNull();
	}

	[Fact]
	void Aggregate_expiry_outside_a_phase_names_no_per_state_overrun()
	{
		using var aggregate = new CancellationTokenSource();
		aggregate.Cancel();
		BrowserPhaseRunner runner = new(aggregate.Token);

		var exception = Should.Throw<BrowserFailure>(runner.ThrowIfAggregateExpired);

		exception.Message.ShouldBe("Aggregate browser-test ceiling expired; no per-state timeout was exceeded.");
	}
}

public sealed class BrowserEvidenceLifecycleTests
{
	[Fact]
	void Evidence_owner_exposes_only_the_scoped_execute_operation()
	{
		var operationalMembers = typeof(BrowserEvidence)
			.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
				BindingFlags.DeclaredOnly)
			.Where(static member => member switch
			{
				MethodInfo method => method.IsPublic || method.IsAssembly,
				PropertyInfo property => property.GetMethod?.IsPublic is true ||
					property.GetMethod?.IsAssembly is true,
				_ => false,
			})
			.Select(static member => member.Name)
			.ToArray();

		operationalMembers.ShouldBe([nameof(BrowserEvidence.ExecuteAsync)]);
		typeof(BrowserEvidence).GetInterfaces().ShouldNotContain(typeof(IAsyncDisposable));
		typeof(BrowserEvidence.BrowserOperation).GetProperty(nameof(BrowserEvidence.BrowserOperation.Page),
			BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull();
		typeof(BrowserEvidence.BrowserOperation)
			.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			.ShouldAllBe(static constructor => !constructor.IsPublic);
		typeof(BrowserEvidence.BrowserOperation)
			.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
				BindingFlags.DeclaredOnly)
			.Where(static method => !method.IsSpecialName && (method.IsPublic || method.IsAssembly))
			.Select(static method => method.Name)
			.ShouldBe([nameof(BrowserEvidence.BrowserOperation.RunPhaseAsync)]);
	}

	[Fact]
	async Task Scoped_operation_rejects_use_after_execute_returns()
	{
		var testName = $"{nameof(Scoped_operation_rejects_use_after_execute_returns)}-{Guid.NewGuid():N}";
		var (evidence, _, _) = await CreateEvidenceAsync(testName);
		BrowserEvidence.BrowserOperation? retainedOperation = null;

		await evidence.ExecuteAsync(operation =>
		{
			retainedOperation = operation;
			return Task.CompletedTask;
		});

		retainedOperation.ShouldNotBeNull();
		Should.Throw<InvalidOperationException>(() => _ = retainedOperation.Page)
			.Message.ShouldContain("only during ExecuteAsync");
	}

	[Fact]
	async Task Evidence_start_removes_only_the_exact_stale_test_directory()
	{
		var testName = $"{nameof(Evidence_start_removes_only_the_exact_stale_test_directory)}-{Guid.NewGuid():N}";
		var siblingName = $"{nameof(Evidence_start_removes_only_the_exact_stale_test_directory)}-sibling-{Guid.NewGuid():N}";
		var directory = Path.Combine(BrowserFailure.ArtifactRoot, testName);
		var siblingDirectory = Path.Combine(BrowserFailure.ArtifactRoot, siblingName);
		Directory.CreateDirectory(directory);
		Directory.CreateDirectory(siblingDirectory);
		await File.WriteAllTextAsync(
			Path.Combine(directory, "stale.log"),
			"stale failure evidence",
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			Path.Combine(siblingDirectory, "sibling.log"),
			"independent failure evidence",
			TestContext.Current.CancellationToken);
		try
		{
			var (evidence, _, _) = await CreateEvidenceAsync(testName);

			Directory.Exists(directory).ShouldBeFalse();
			File.Exists(Path.Combine(siblingDirectory, "sibling.log")).ShouldBeTrue();
			await evidence.ExecuteAsync(static _ => Task.CompletedTask);
		}
		finally
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
			if (Directory.Exists(siblingDirectory))
				Directory.Delete(siblingDirectory, recursive: true);
		}
	}

	[Fact]
	async Task Evidence_start_succeeds_when_the_exact_test_directory_is_absent()
	{
		var testName = $"{nameof(Evidence_start_succeeds_when_the_exact_test_directory_is_absent)}-{Guid.NewGuid():N}";
		var directory = Path.Combine(BrowserFailure.ArtifactRoot, testName);
		Directory.Exists(directory).ShouldBeFalse();

		var (evidence, _, _) = await CreateEvidenceAsync(testName);
		await evidence.ExecuteAsync(static _ => Task.CompletedTask);

		Directory.Exists(directory).ShouldBeFalse();
	}

	[Fact]
	async Task Evidence_start_surfaces_a_blocking_file_at_the_exact_artifact_path()
	{
		var testName =
			$"{nameof(Evidence_start_surfaces_a_blocking_file_at_the_exact_artifact_path)}-{Guid.NewGuid():N}";
		var path = Path.Combine(BrowserFailure.ArtifactRoot, testName);
		Directory.CreateDirectory(BrowserFailure.ArtifactRoot);
		await File.WriteAllTextAsync(
			path,
			"blocks stale artifact cleanup",
			TestContext.Current.CancellationToken);
		var (context, page) = CreateEvidenceContext();
		try
		{
			var exception = await Should.ThrowAsync<IOException>(() => BrowserEvidence.StartAsync(
				context,
				testName,
				new Uri("http://127.0.0.1:54321"),
				new(),
				static _ => false,
				CancellationToken.None));

			exception.Message.ShouldContain("exists as a file");
			await context.Received(1).DisposeAsync();
			await page.DidNotReceive().CloseAsync(Arg.Any<PageCloseOptions>());
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	async Task Execute_preserves_a_named_phase_failure_and_flushes_evidence()
	{
		var testName = $"{nameof(Execute_preserves_a_named_phase_failure_and_flushes_evidence)}-{Guid.NewGuid():N}";
		var directory = Path.Combine(BrowserFailure.ArtifactRoot, testName);
		try
		{
			var exception = await Should.ThrowAsync<BrowserFailure>(async () =>
			{
				var (evidence, _, _) = await CreateEvidenceAsync(testName);
				await evidence.ExecuteAsync(operation => operation.RunPhaseAsync(
						"framework warm-up",
						TimeSpan.FromMilliseconds(50),
						cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)));
			});

			exception.Message.ShouldContain("Browser phase 'framework warm-up' timed out");
			File.Exists(Path.Combine(directory, "browser.log")).ShouldBeTrue();
		}
		finally
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	async Task Callback_recording_failure_is_a_completion_failure()
	{
		var testName = $"{nameof(Callback_recording_failure_is_a_completion_failure)}-{Guid.NewGuid():N}";
		var directory = Path.Combine(BrowserFailure.ArtifactRoot, testName);
		try
		{
			var (evidence, page, _) = await CreateEvidenceAsync(testName);
			var message = Substitute.For<IConsoleMessage>();
			message.Type.Returns(_ => throw new InvalidOperationException("console shape failed"));
			page.Console += Raise.Event<EventHandler<IConsoleMessage>>(page, message);

			var exception = await Should.ThrowAsync<BrowserFailure>(() =>
				evidence.ExecuteAsync(static _ => Task.CompletedTask));

			exception.Message.ShouldContain("evidence callback recording failure");
		}
		finally
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	async Task Execute_records_an_assertion_failure_without_replacing_it_during_disposal()
	{
		var testName = $"{nameof(Execute_records_an_assertion_failure_without_replacing_it_during_disposal)}-{Guid.NewGuid():N}";
		var directory = Path.Combine(BrowserFailure.ArtifactRoot, testName);
		try
		{
			var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
			{
				var (evidence, _, _) = await CreateEvidenceAsync(testName);
				await evidence.ExecuteAsync(static _ =>
					throw new InvalidOperationException("assertion stayed primary"));
			});

			exception.Message.ShouldBe("assertion stayed primary");
			var browserLog = await File.ReadAllTextAsync(
				Path.Combine(directory, "browser.log"),
				TestContext.Current.CancellationToken);
			browserLog.ShouldContain("assertion stayed primary");
		}
		finally
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	async Task Operation_failure_preserves_primary_and_records_context_shutdown_failure()
	{
		var testName =
			$"{nameof(Operation_failure_preserves_primary_and_records_context_shutdown_failure)}-{Guid.NewGuid():N}";
		var directory = Path.Combine(BrowserFailure.ArtifactRoot, testName);
		try
		{
			var (evidence, _, context) = await CreateEvidenceAsync(testName);
			var primaryFailure = new InvalidOperationException("distinctive primary operation failure");
			var shutdownFailure = new IOException("distinctive context shutdown failure");
			context.DisposeAsync().Returns(_ => ValueTask.FromException(shutdownFailure));

			var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
				evidence.ExecuteAsync(_ => throw primaryFailure));

			exception.ShouldBeSameAs(primaryFailure);
			exception.Message.ShouldBe("distinctive primary operation failure");
			var cleanupDiagnostic = exception.Data[BrowserEvidence.OperationFailureCleanupDiagnosticKey]
				.ShouldBeOfType<AggregateException>();
			cleanupDiagnostic.InnerExceptions.Count.ShouldBe(1);
			cleanupDiagnostic.InnerExceptions[0].ShouldBeSameAs(shutdownFailure);
			var browserLog = await File.ReadAllTextAsync(
				Path.Combine(directory, "browser.log"),
				TestContext.Current.CancellationToken);
			browserLog.ShouldContain("distinctive context shutdown failure");
			await context.Received(1).DisposeAsync();
		}
		finally
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	async Task Page_error_delivered_while_closing_is_in_the_final_snapshot()
	{
		var testName = $"{nameof(Page_error_delivered_while_closing_is_in_the_final_snapshot)}-{Guid.NewGuid():N}";
		var directory = Path.Combine(BrowserFailure.ArtifactRoot, testName);
		try
		{
			var (evidence, page, _) = await CreateEvidenceAsync(testName);
			page.CloseAsync(Arg.Any<PageCloseOptions>()).Returns(_ =>
			{
				page.PageError += Raise.Event<EventHandler<string>>(page, "late page failure");
				return Task.CompletedTask;
			});

			var exception = await Should.ThrowAsync<BrowserFailure>(() =>
				evidence.ExecuteAsync(static _ => Task.CompletedTask));

			exception.Message.ShouldContain("uncaught page error");
		}
		finally
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	async Task Failed_evidence_startup_disposes_the_context()
	{
		var context = Substitute.For<IBrowserContext>();
		var tracing = Substitute.For<ITracing>();
		var disposed = 0;
		context.Tracing.Returns(tracing);
		tracing.StartAsync(Arg.Any<TracingStartOptions>())
			.Returns(Task.FromException(new InvalidOperationException("trace startup failed")));
		context.DisposeAsync().Returns(_ =>
		{
			Interlocked.Increment(ref disposed);
			return ValueTask.CompletedTask;
		});

		var exception = await Should.ThrowAsync<InvalidOperationException>(() => BrowserEvidence.StartAsync(
			context,
			nameof(Failed_evidence_startup_disposes_the_context),
			new Uri("http://127.0.0.1:54321"),
			new(),
			static _ => false,
			CancellationToken.None));

		exception.Message.ShouldBe("trace startup failed");
		disposed.ShouldBe(1);
	}

	[Fact]
	async Task Failed_page_startup_disposes_the_traced_context()
	{
		var context = Substitute.For<IBrowserContext>();
		var tracing = Substitute.For<ITracing>();
		var disposed = 0;
		context.Tracing.Returns(tracing);
		context.NewPageAsync().Returns(Task.FromException<IPage>(
			new InvalidOperationException("page startup failed")));
		context.DisposeAsync().Returns(_ =>
		{
			Interlocked.Increment(ref disposed);
			return ValueTask.CompletedTask;
		});

		var exception = await Should.ThrowAsync<InvalidOperationException>(() => BrowserEvidence.StartAsync(
			context,
			nameof(Failed_page_startup_disposes_the_traced_context),
			new Uri("http://127.0.0.1:54321"),
			new(),
			static _ => false,
			CancellationToken.None));

		exception.Message.ShouldBe("page startup failed");
		disposed.ShouldBe(1);
	}

	[Fact]
	async Task Artifact_write_failure_still_closes_context_without_replacing_the_test_failure()
	{
		var testName = $"{nameof(Artifact_write_failure_still_closes_context_without_replacing_the_test_failure)}-{Guid.NewGuid():N}";
		var blockedDirectory = Path.Combine(BrowserFailure.ArtifactRoot, testName);
		try
		{
			var (evidence, _, context) = await CreateEvidenceAsync(testName);
			Directory.CreateDirectory(BrowserFailure.ArtifactRoot);
			await File.WriteAllTextAsync(
				blockedDirectory,
				"blocks artifact directory creation",
				TestContext.Current.CancellationToken);

			var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
				evidence.ExecuteAsync(static _ =>
					throw new InvalidOperationException("test failure stayed primary")));

			exception.Message.ShouldBe("test failure stayed primary");
			await context.Received(1).DisposeAsync();
		}
		finally
		{
			File.Delete(blockedDirectory);
		}
	}

	[Fact]
	async Task Execute_accepts_the_exact_redirect_policy()
	{
		var testName = $"{nameof(Execute_accepts_the_exact_redirect_policy)}-{Guid.NewGuid():N}";
		var (evidence, page, _) = await CreateEvidenceAsync(testName);
		var request = Substitute.For<IRequest>();
		var response = Substitute.For<IResponse>();
		request.Method.Returns("GET");
		response.Request.Returns(request);
		response.Status.Returns(302);
		response.Url.Returns("http://127.0.0.1:54321/sign-in");

		await evidence.ExecuteAsync(
			_ =>
			{
				page.Response += Raise.Event<EventHandler<IResponse>>(page, response);
				return Task.CompletedTask;
			},
			new ExactRedirectPolicy(response));
	}

	[Fact]
	async Task Http_304_cache_validation_is_not_a_redirect_but_unapproved_302_is()
	{
		var testName =
			$"{nameof(Http_304_cache_validation_is_not_a_redirect_but_unapproved_302_is)}-{Guid.NewGuid():N}";
		var directory = Path.Combine(BrowserFailure.ArtifactRoot, testName);
		try
		{
			var (evidence, page, _) = await CreateEvidenceAsync(testName);
			var cacheRequest = Substitute.For<IRequest>();
			cacheRequest.Method.Returns("GET");
			var cacheValidation = Substitute.For<IResponse>();
			cacheValidation.Request.Returns(cacheRequest);
			cacheValidation.Status.Returns(304);
			cacheValidation.Url.Returns("http://127.0.0.1:54321/cached.css");
			var redirectRequest = Substitute.For<IRequest>();
			redirectRequest.Method.Returns("GET");
			var redirect = Substitute.For<IResponse>();
			redirect.Request.Returns(redirectRequest);
			redirect.Status.Returns(302);
			redirect.Url.Returns("http://127.0.0.1:54321/sign-in");

			var exception = await Should.ThrowAsync<BrowserFailure>(() => evidence.ExecuteAsync(_ =>
			{
				page.Response += Raise.Event<EventHandler<IResponse>>(page, cacheValidation);
				page.Response += Raise.Event<EventHandler<IResponse>>(page, redirect);
				return Task.CompletedTask;
			}));

			exception.Message.ShouldContain("1 unexpected first-party redirect(s)");
		}
		finally
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}
	}

	static async Task<(BrowserEvidence Evidence, IPage Page, IBrowserContext Context)> CreateEvidenceAsync(
		string testName)
	{
		var (context, page) = CreateEvidenceContext();

		var evidence = await BrowserEvidence.StartAsync(
			context,
			testName,
			new Uri("http://127.0.0.1:54321"),
			new(),
			static _ => false,
			CancellationToken.None);
		return (evidence, page, context);
	}

	static (IBrowserContext Context, IPage Page) CreateEvidenceContext()
	{
		var context = Substitute.For<IBrowserContext>();
		var tracing = Substitute.For<ITracing>();
		var page = Substitute.For<IPage>();
		context.Tracing.Returns(tracing);
		context.NewPageAsync().Returns(Task.FromResult(page));
		page.Frames.Returns([]);
		page.Url.Returns("about:blank");
		return (context, page);
	}

	sealed class ExactRedirectPolicy(IResponse expected) : BrowserEvidencePolicy("exact redirect")
	{
		internal override bool IsExpectedRedirect(IResponse response) => ReferenceEquals(response, expected);
	}
}

public sealed class BrowserHostFixtureLifecycleTests
{
	[Fact]
	async Task Cleanup_attempts_every_resource_and_preserves_each_failure()
	{
		List<string> attempts = [];

		var failures = await BrowserFixtureCleanup.CollectAsync(
			Cleanup("browser", new InvalidOperationException("browser cleanup failed")),
			Cleanup("playwright"),
			Cleanup("factory", new IOException("factory cleanup failed")),
			Cleanup("lease"),
			Cleanup("aggregate"));

		attempts.ShouldBe(["browser", "playwright", "factory", "lease", "aggregate"]);
		failures.Select(static failure => failure.Message).ShouldBe([
			"browser cleanup failed",
			"factory cleanup failed",
		]);

		Func<ValueTask> Cleanup(string resource, Exception? failure = null) => () =>
		{
			attempts.Add(resource);
			return failure is null ? ValueTask.CompletedTask : ValueTask.FromException(failure);
		};
	}
}

public sealed class FrameworkRequestQuiescenceTests
{
	[Fact]
	async Task Framework_request_cannot_enter_between_the_final_decision_and_unsubscribe()
	{
		var origin = new Uri("http://127.0.0.1:54321");
		FrameworkRequestActivity activity = new(origin);
		var wasmRequest = FrameworkRequest("/_framework/runtime.wasm");
		var wasmResponse = Substitute.For<IResponse>();
		wasmResponse.Ok.Returns(true);
		wasmResponse.Url.Returns(new Uri(origin, "/_framework/runtime.wasm").AbsoluteUri);
		activity.Started(wasmRequest).ShouldBeTrue();
		activity.Responded(wasmResponse);
		activity.Finished(wasmRequest);
		await Task.Delay(TimeSpan.FromMilliseconds(550), TestContext.Current.CancellationToken);
		var boundaryRequest = FrameworkRequest("/_framework/late.wasm");
		var acceptedDuringUnsubscribe = true;

		var completed = activity.TryComplete(() =>
			acceptedDuringUnsubscribe = activity.Started(boundaryRequest));

		completed.ShouldBeTrue();
		acceptedDuringUnsubscribe.ShouldBeFalse();
	}

	[Fact]
	async Task Cancellation_ends_navigation_without_waiting_for_the_Playwright_timeout()
	{
		var page = Substitute.For<IPage>();
		var navigation = new TaskCompletionSource<IResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
		page.GotoAsync("/", Arg.Any<PageGotoOptions>()).Returns(navigation.Task);
		page.CloseAsync(Arg.Any<PageCloseOptions>()).Returns(_ =>
		{
			navigation.TrySetException(new PlaywrightException("navigation aborted by page close"));
			return Task.FromException(new PlaywrightException("page close diagnostic"));
		});
		BrowserPhaseRunner runner = new(CancellationToken.None);

		var exception = await Should.ThrowAsync<BrowserFailure>(() => runner.RunAsync(
			"framework warm-up",
			TimeSpan.FromMilliseconds(50),
			cancellationToken => FrameworkRequestQuiescence.WaitAsync(
				page,
				new Uri("http://127.0.0.1:54321"),
				cancellationToken)));

		exception.Message.ShouldContain("Browser phase 'framework warm-up' timed out");
		await page.Received(1).CloseAsync(Arg.Any<PageCloseOptions>());
		navigation.Task.IsFaulted.ShouldBeTrue();
		exception.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
		var cancellation = (OperationCanceledException)exception.InnerException!;
		var cleanupFailures = cancellation.Data[FrameworkRequestQuiescence.CleanupDiagnosticKey]
			.ShouldBeOfType<AggregateException>();
		cleanupFailures.InnerExceptions.Count.ShouldBe(2);
	}

	static IRequest FrameworkRequest(string path)
	{
		var request = Substitute.For<IRequest>();
		request.Url.Returns(new Uri(new Uri("http://127.0.0.1:54321"), path).AbsoluteUri);
		return request;
	}
}
