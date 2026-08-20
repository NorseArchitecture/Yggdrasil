using Microsoft.Playwright;
using Norse.Hosting.BrowserTesting;
using Norse.Hosting.Stories.Server.Tests.BrowserRuntime;

namespace Norse.Hosting.Stories.Server.Tests;

public sealed class BrowserHostFixtureTests
{
	[Fact]
	async Task Xunit_initialization_is_inert_until_the_first_browser_operation()
	{
		await using StoriesBrowserHostFixture fixture = new();

		await fixture.InitializeAsync();

		Should.Throw<InvalidOperationException>(() => fixture.Origin)
			.Message.ShouldBe("Kestrel origin is unavailable before fixture initialization.");
	}

	[Fact(Explicit = true, Timeout = 300_000)]
	async Task Fixture_serves_the_real_host_through_an_ephemeral_Kestrel_origin()
	{
		await using StoriesBrowserHostFixture fixture = new();
		await fixture.InitializeAsync();

		var evidence = await fixture.OpenEvidenceAsync(
			nameof(Fixture_serves_the_real_host_through_an_ephemeral_Kestrel_origin));
		fixture.Origin.Scheme.ShouldBe(Uri.UriSchemeHttp);
		fixture.Origin.IsDefaultPort.ShouldBeFalse();
		await evidence.ExecuteAsync(async operation =>
		{
			IResponse? response = null;
			await operation.RunPhaseAsync(
				"host startup",
				BrowserTimeouts.HostStartup,
				async cancellationToken => response = await operation.Page.GotoAsync("/", new()
				{
					WaitUntil = WaitUntilState.DOMContentLoaded,
					Timeout = BrowserTimeouts.PlaywrightHostStartupMilliseconds,
				}).WaitAsync(cancellationToken));

			response.ShouldNotBeNull();
			response.Status.ShouldBe(200);
			var browserLanguage = await operation.Page.EvaluateAsync<string>("navigator.language");
			browserLanguage.ShouldBe("en-US");
		}, new StoriesBrowserEvidencePolicy(fixture.Origin)).WaitAsync(TestContext.Current.CancellationToken);
	}
}

public sealed class BrowserFixtureStartupTests
{
	[Fact]
	async Task Concurrent_browser_operations_share_one_startup()
	{
		var releaseStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var startupCount = 0;
		await using BrowserFixtureStartup startup = new(
			async () =>
			{
				Interlocked.Increment(ref startupCount);
				await releaseStartup.Task;
			},
			static () => ValueTask.CompletedTask);

		var first = startup.EnsureStartedAsync();
		var second = startup.EnsureStartedAsync();

		startupCount.ShouldBe(1);
		releaseStartup.SetResult();
		await Task.WhenAll(first, second);
		startupCount.ShouldBe(1);
	}

	[Fact]
	async Task Startup_failure_is_preserved_and_never_retried()
	{
		var failure = new InvalidOperationException("controlled startup failure");
		var startupCount = 0;
		await using BrowserFixtureStartup startup = new(
			() =>
			{
				Interlocked.Increment(ref startupCount);
				return Task.FromException(failure);
			},
			static () => ValueTask.CompletedTask);

		var first = await Should.ThrowAsync<InvalidOperationException>(startup.EnsureStartedAsync);
		var second = await Should.ThrowAsync<InvalidOperationException>(startup.EnsureStartedAsync);

		first.ShouldBeSameAs(failure);
		second.ShouldBeSameAs(failure);
		startupCount.ShouldBe(1);
	}

	[Fact]
	async Task Synchronous_startup_failure_is_preserved_and_never_retried()
	{
		var failure = new InvalidOperationException("controlled synchronous startup failure");
		var startupCount = 0;
		await using BrowserFixtureStartup startup = new(
			() =>
			{
				Interlocked.Increment(ref startupCount);
				throw failure;
			},
			static () => ValueTask.CompletedTask);

		var first = await Should.ThrowAsync<InvalidOperationException>(startup.EnsureStartedAsync);
		var second = await Should.ThrowAsync<InvalidOperationException>(startup.EnsureStartedAsync);

		first.ShouldBeSameAs(failure);
		second.ShouldBeSameAs(failure);
		startupCount.ShouldBe(1);
	}

	[Fact]
	async Task Disposal_before_start_is_idempotent_and_rejects_later_operations()
	{
		int
			startupCount = 0,
			disposalCount = 0;
		await using BrowserFixtureStartup startup = new(
			() =>
			{
				Interlocked.Increment(ref startupCount);
				return Task.CompletedTask;
			},
			() =>
			{
				Interlocked.Increment(ref disposalCount);
				return ValueTask.CompletedTask;
			});

		await startup.DisposeAsync();
		await startup.DisposeAsync();

		startupCount.ShouldBe(0);
		disposalCount.ShouldBe(1);
		await Should.ThrowAsync<ObjectDisposedException>(startup.EnsureStartedAsync);
		startupCount.ShouldBe(0);
		disposalCount.ShouldBe(1);
	}

	[Fact]
	async Task Disposal_after_completed_start_is_idempotent_and_rejects_later_operations()
	{
		int
			startupCount = 0,
			disposalCount = 0;
		await using BrowserFixtureStartup startup = new(
			() =>
			{
				Interlocked.Increment(ref startupCount);
				return Task.CompletedTask;
			},
			() =>
			{
				Interlocked.Increment(ref disposalCount);
				return ValueTask.CompletedTask;
			});

		await startup.EnsureStartedAsync();
		await startup.DisposeAsync();
		await startup.DisposeAsync();

		startupCount.ShouldBe(1);
		disposalCount.ShouldBe(1);
		await Should.ThrowAsync<ObjectDisposedException>(startup.EnsureStartedAsync);
		startupCount.ShouldBe(1);
		disposalCount.ShouldBe(1);
	}

	[Fact]
	async Task Disposal_waits_for_inflight_startup_and_rejects_later_operations()
	{
		var releaseStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var disposalCount = 0;
		await using BrowserFixtureStartup startup = new(
			() => releaseStartup.Task,
			() =>
			{
				Interlocked.Increment(ref disposalCount);
				return ValueTask.CompletedTask;
			});
		var operation = startup.EnsureStartedAsync();

		var disposal = startup.DisposeAsync().AsTask();

		disposal.IsCompleted.ShouldBeFalse();
		disposalCount.ShouldBe(0);
		releaseStartup.SetResult();
		await operation;
		await disposal;
		disposalCount.ShouldBe(1);
		await Should.ThrowAsync<ObjectDisposedException>(startup.EnsureStartedAsync);
	}
}

sealed class StoriesBrowserHostFixture : BrowserHostFixture<Program>;
