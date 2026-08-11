using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Norse.Hosting.BrowserTesting;

sealed class BrowserLeaseWaitException(TimeSpan elapsed, string owner, Exception innerException) :
	TimeoutException(
		string.Create(
			CultureInfo.InvariantCulture,
			$"Browser lease phase ended after waiting {elapsed.TotalSeconds:F1}s; holder {owner}."),
		innerException);

sealed class BrowserProcessLease(FileStream stream) : IAsyncDisposable
{
	const string FileName = "norse-yggdrasil-playwright.lock";
	const string UnknownOwner = "unknown";

	[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
		Justification = "BrowserProcessLease takes ownership of the stream and disposes it asynchronously.")]
	internal static async ValueTask<BrowserProcessLease> AcquireAsync(CancellationToken cancellationToken)
	{
		var path = Path.Combine(Path.GetTempPath(), FileName);
		var ownerPath = $"{path}.owner";
		var wait = Stopwatch.StartNew();
		var contended = false;
		var owner = UnknownOwner;
		while (true)
		{
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
			}
			catch (OperationCanceledException exception) when (contended)
			{
				throw new BrowserLeaseWaitException(wait.Elapsed, owner, exception);
			}

			FileStream stream;
			try
			{
				stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			}
			catch (IOException)
			{
				contended = true;
				owner = ReadOwner(ownerPath);
				try
				{
					await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
				}
				catch (OperationCanceledException exception)
				{
					throw new BrowserLeaseWaitException(wait.Elapsed, owner, exception);
				}
				continue;
			}

			try
			{
				owner = $"pid={Environment.ProcessId}, acquiredUtc={DateTimeOffset.UtcNow:O}";
				await File.WriteAllTextAsync(ownerPath, owner, cancellationToken);
				Console.WriteLine($"Browser lease acquired: {owner}");
				return new(stream);
			}
			catch
			{
				await stream.DisposeAsync();
				throw;
			}
		}
	}

	public async ValueTask DisposeAsync()
	{
		await stream.DisposeAsync();
		Console.WriteLine($"Browser lease released: pid={Environment.ProcessId}, utc={DateTimeOffset.UtcNow:O}");
	}

	static string ReadOwner(string path)
	{
		try
		{
			return File.ReadAllText(path);
		}
		catch (IOException)
		{
			return UnknownOwner;
		}
	}
}
