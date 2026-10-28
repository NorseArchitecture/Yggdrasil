using Norse.Hosting.BrowserTesting;

namespace Norse.Hosting.Stories.Tests;

public sealed class BrowserProcessLeaseTests
{
	[Fact]
	async Task A_second_browser_process_waits_until_the_first_lease_releases()
	{
		var ownerPath = Path.Combine(Path.GetTempPath(), "norse-yggdrasil-playwright.lock.owner");
		string? firstOwner = null;
		var first = await BrowserProcessLease.AcquireAsync(TestContext.Current.CancellationToken);
		try
		{
			first.ShouldNotBeAssignableTo<IDisposable>();
			File.Exists(ownerPath).ShouldBeTrue();
			firstOwner = await File.ReadAllTextAsync(ownerPath, TestContext.Current.CancellationToken);
			firstOwner.ShouldContain($"pid={Environment.ProcessId}");

			// Start the second acquire before arming the timeout: it runs synchronously to its first wait, so it
			// has already met the held lock when the token fires. A timeout armed first can fire before a
			// starved thread pool runs the call, and a never-contended acquire answers a bare cancellation.
			using var blocked = new CancellationTokenSource();
			var waiting = BrowserProcessLease.AcquireAsync(blocked.Token).AsTask();
			blocked.CancelAfter(TimeSpan.FromMilliseconds(250));
			var exception = await Should.ThrowAsync<BrowserLeaseWaitException>(waiting);
			exception.Message.ShouldContain("Browser lease");
			exception.Message.ShouldContain($"pid={Environment.ProcessId}");
		}
		finally
		{
			await first.DisposeAsync();
		}

		await using var second = await BrowserProcessLease.AcquireAsync(TestContext.Current.CancellationToken);
		second.ShouldNotBeNull();
		var secondOwner = await File.ReadAllTextAsync(ownerPath, TestContext.Current.CancellationToken);
		secondOwner.ShouldContain($"pid={Environment.ProcessId}");
		secondOwner.ShouldNotBe(firstOwner);
	}
}
