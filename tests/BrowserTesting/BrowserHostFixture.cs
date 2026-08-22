using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Norse.Hosting.BrowserTesting;

abstract class BrowserHostFixture<TEntryPoint> : IAsyncLifetime where TEntryPoint : class
{
	readonly BrowserFixtureStartup _startup;
	[SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
		Justification = "ReleaseResourcesAsync disposes the aggregate through the independent cleanup pipeline.")]
	readonly CancellationTokenSource _aggregate = new(BrowserTimeouts.Test);
	readonly ConcurrentQueue<string> _serverLog = new();
	BrowserProcessLease? _lease;
	[SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
		Justification = "ReleaseResourcesAsync disposes the factory through the independent cleanup pipeline.")]
	WebApplicationFactory<TEntryPoint>? _factory;
	IPlaywright? _playwright;
	IBrowser? _browser;
	bool _aggregateDisposed;
	[SuppressMessage("Style", "IDE0032:Use auto property",
		Justification = "The nullable backing field preserves the required pre-initialization exception contract.")]
	Uri? _origin;

	protected BrowserHostFixture() =>
		_startup = new(StartResourcesAsync, DisposeResourcesAsync);

	internal Uri Origin =>
		_origin ?? throw new InvalidOperationException("Kestrel origin is unavailable before fixture initialization.");

	/// <summary>
	///     The booted host's root service provider -- for a fact that needs to reach into the running
	///     Kestrel instance's own DI container (e.g. to decode a cookie through its Data Protection key ring)
	///     rather than only drive it through HTTP/the browser. Ensures the fixture has started, mirroring
	///     <see cref="OpenEvidenceAsync" />.
	/// </summary>
	internal async Task<IServiceProvider> GetServicesAsync()
	{
		await _startup.EnsureStartedAsync();
		return _factory?.Services ??
			throw new InvalidOperationException("Kestrel host services are unavailable before fixture initialization.");
	}

	protected virtual void ConfigureWebHost(IWebHostBuilder builder) { }
	protected virtual bool IsExpectedRedirect(IResponse response) => false;

	public ValueTask InitializeAsync() => ValueTask.CompletedTask;

	async Task StartResourcesAsync()
	{
		try
		{
			try
			{
				_lease = await BrowserProcessLease.AcquireAsync(_aggregate.Token);
			}
			catch (BrowserLeaseWaitException exception)
			{
				var host = typeof(TEntryPoint).Assembly.GetName().Name ??
					throw new InvalidOperationException("Host entry-point assembly has no name.");
				throw BrowserFailure.WriteStartupFailure(host, "browser lease", exception);
			}

			_factory = new ConfigurableFactory<TEntryPoint>(builder =>
			{
				ConfigureWebHost(builder);
				builder.ConfigureLogging(logging => logging.AddProvider(new BrowserServerLogProvider(_serverLog)));
			});
			_factory.UseKestrel(0);

			_factory.ClientOptions.AllowAutoRedirect = false;
			using var client = _factory.CreateClient();
			_origin = client.BaseAddress ?? throw new InvalidOperationException("Kestrel exposed no origin.");

			_playwright = await Playwright.CreateAsync();
			_browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
		}
		catch (Exception startupException)
		{
			var cleanupFailures = await ReleaseResourcesAsync();
			if (cleanupFailures.Count > 0)
				throw new AggregateException(
					"Browser fixture initialization and cleanup failed.",
					new[] { startupException }.Concat(cleanupFailures));
			throw;
		}
	}

	/// <param name="testName">Identifies this evidence run's artifact directory.</param>
	/// <param name="cookies">
	///     Seeded into the context's cookie jar before its first page is created -- the only way a Playwright
	///     context can carry an already-minted cookie (e.g. one read back from a prior <see cref="HttpClient" />
	///     handshake against the same host) instead of minting its own on first contact. Cookie-seeding lives
	///     here, before <see cref="BrowserEvidence" /> exists, rather than as a method on
	///     <see cref="BrowserEvidence" /> itself -- that type's public surface is deliberately locked to
	///     exactly <see cref="BrowserEvidence.ExecuteAsync" /> (see its own reflection-guarded test).
	/// </param>
	internal async Task<BrowserEvidence> OpenEvidenceAsync(
		string testName, IEnumerable<Microsoft.Playwright.Cookie>? cookies = null)
	{
		await _startup.EnsureStartedAsync();
		var browser = _browser ??
			throw new InvalidOperationException("Chromium is unavailable before fixture initialization.");
		var context = await browser.NewContextAsync(new()
		{
			BaseURL = Origin.AbsoluteUri,
			IgnoreHTTPSErrors = false,
			Locale = "en-US",
		});
		if (cookies is not null)
			await context.AddCookiesAsync(cookies);
		return await BrowserEvidence.StartAsync(
			context,
			testName,
			Origin,
			_serverLog,
			IsExpectedRedirect,
			_aggregate.Token);
	}

	public ValueTask DisposeAsync() => _startup.DisposeAsync();

	async ValueTask DisposeResourcesAsync()
	{
		var failures = await ReleaseResourcesAsync();
		if (failures.Count > 0)
			throw new AggregateException("Browser fixture cleanup failed.", failures);
	}

	async Task<IReadOnlyList<Exception>> ReleaseResourcesAsync()
	{
		return await BrowserFixtureCleanup.CollectAsync(
			DisposeBrowserAsync,
			DisposePlaywrightAsync,
			DisposeFactoryAsync,
			DisposeLeaseAsync,
			DisposeAggregateAsync);

		async ValueTask DisposeBrowserAsync()
		{
			if (_browser is null)
				return;
			await _browser.DisposeAsync();
			_browser = null;
		}

		ValueTask DisposePlaywrightAsync()
		{
			if (_playwright is null)
				return ValueTask.CompletedTask;
			_playwright.Dispose();
			_playwright = null;
			return ValueTask.CompletedTask;
		}

		async ValueTask DisposeFactoryAsync()
		{
			if (_factory is null)
				return;
			await _factory.DisposeAsync();
			_factory = null;
			_origin = null;
		}

		async ValueTask DisposeLeaseAsync()
		{
			if (_lease is null)
				return;
			await _lease.DisposeAsync();
			_lease = null;
		}

		ValueTask DisposeAggregateAsync()
		{
			if (_aggregateDisposed)
				return ValueTask.CompletedTask;
			_aggregate.Dispose();
			_aggregateDisposed = true;
			return ValueTask.CompletedTask;
		}
	}
}

sealed class BrowserFixtureStartup(
	Func<Task> start,
	Func<ValueTask> dispose) : IAsyncDisposable
{
	readonly Lock _gate = new();
	Task? _startup;
	Task? _disposal;

	internal Task EnsureStartedAsync()
	{
		lock (_gate)
		{
			if (_disposal is not null)
				return Task.FromException(
					new ObjectDisposedException(nameof(BrowserFixtureStartup)));
			return _startup ??= StartAsync();
		}
	}

	public ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			_disposal ??= DisposeAsync(_startup);
			return new(_disposal);
		}
	}

	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "Synchronous startup failures must be cached exactly like asynchronous startup failures.")]
	Task StartAsync()
	{
		try
		{
			return start();
		}
		catch (Exception exception)
		{
			return Task.FromException(exception);
		}
	}

	async Task DisposeAsync(Task? startup)
	{
		if (startup is not null)
			await startup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
		await dispose();
	}
}

sealed class ConfigurableFactory<TEntryPoint>(Action<IWebHostBuilder> configureWebHost) :
	WebApplicationFactory<TEntryPoint> where TEntryPoint : class
{
	protected override void ConfigureWebHost(IWebHostBuilder builder) => configureWebHost(builder);
}

static class BrowserFixtureCleanup
{
	[SuppressMessage("Design", "CA1031:Do not catch general exception types",
		Justification = "Every fixture resource, especially the global lease, must be released independently.")]
	internal static async Task<IReadOnlyList<Exception>> CollectAsync(params Func<ValueTask>[] cleanupActions)
	{
		List<Exception> failures = [];
		foreach (var cleanup in cleanupActions)
		{
			try
			{
				await cleanup();
			}
			catch (Exception exception)
			{
				failures.Add(exception);
			}
		}
		return failures;
	}
}
