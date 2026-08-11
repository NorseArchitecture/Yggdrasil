using Norse.Hosting.BrowserTesting;

namespace Norse.Hosting.Stories.Server.Tests;

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

			using var blocked = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
			var exception = await Should.ThrowAsync<BrowserLeaseWaitException>(() =>
				BrowserProcessLease.AcquireAsync(blocked.Token).AsTask());
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
