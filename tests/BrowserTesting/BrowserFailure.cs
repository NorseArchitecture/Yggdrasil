using System.Globalization;

namespace Norse.Hosting.BrowserTesting;

sealed class BrowserFailure(string message, Exception? innerException = null) :
	Exception(message, innerException)
{
	internal static string ArtifactRoot { get; } =
		Path.Combine(AppContext.BaseDirectory, "TestResults", "playwright");

	internal static void RemovePriorArtifacts(string testName)
	{
		var directory = Path.Combine(ArtifactRoot, testName);
		if (File.Exists(directory))
			throw new IOException($"Browser artifact path exists as a file and cannot be prepared: {directory}");
		try
		{
			Directory.Delete(directory, recursive: true);
		}
		catch (DirectoryNotFoundException)
		{
			return;
		}

		if (Directory.Exists(directory) || File.Exists(directory))
			throw new IOException($"Prior browser artifact directory could not be removed: {directory}");
	}

	internal static BrowserFailure AggregateTimeout() =>
		new("Aggregate browser-test ceiling expired; no per-state timeout was exceeded.");

	internal static BrowserFailure AggregateTimeoutDuringPhase(
		string phase,
		TimeSpan elapsed,
		TimeSpan phaseBudget,
		bool phaseBudgetExpired,
		Exception exception)
	{
		var phaseVerdict = phaseBudgetExpired ?
			string.Create(
				CultureInfo.InvariantCulture,
				$"phase budget {phaseBudget.TotalSeconds:F1}s also expired before cancellation was observed") :
			string.Create(
				CultureInfo.InvariantCulture,
				$"phase budget {phaseBudget.TotalSeconds:F1}s was not exceeded");
		return new(
			string.Create(
				CultureInfo.InvariantCulture,
				$"Aggregate browser-test ceiling expired during phase '{phase}' after {elapsed.TotalSeconds:F1}s; {phaseVerdict}."),
			exception);
	}

	internal static BrowserFailure PhaseTimeout(
		string phase,
		TimeSpan elapsed,
		TimeSpan budget,
		Exception exception) =>
		new(
			string.Create(
				CultureInfo.InvariantCulture,
				$"Browser phase '{phase}' timed out after {elapsed.TotalSeconds:F1}s (budget {budget.TotalSeconds:F1}s)."),
			exception);

	internal static BrowserFailure WriteStartupFailure(
		string host,
		string phase,
		Exception exception)
	{
		var directory = Path.Combine(ArtifactRoot, host);
		Directory.CreateDirectory(directory);
		var message = $"Browser startup phase '{phase}' failed: {exception.Message}";
		File.WriteAllText(Path.Combine(directory, "startup.log"), message);
		return new(message, exception);
	}
}
