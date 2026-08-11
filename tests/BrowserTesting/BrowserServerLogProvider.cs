using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Norse.Hosting.BrowserTesting;

sealed class BrowserServerLogProvider(ConcurrentQueue<string> entries) : ILoggerProvider
{
	public ILogger CreateLogger(string categoryName) => new BrowserServerLogger(categoryName, entries);

	public void Dispose() => GC.SuppressFinalize(this);

	sealed class BrowserServerLogger(string categoryName, ConcurrentQueue<string> entries) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

		public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			if (!IsEnabled(logLevel))
				return;

			var message = formatter(state, exception);
			entries.Enqueue(string.Create(
				CultureInfo.InvariantCulture,
				$"{DateTimeOffset.UtcNow:O} [{logLevel}] {categoryName} ({eventId.Id}): {message}{FormatException(exception)}"));
		}

		static string FormatException(Exception? exception) => exception is null ? string.Empty : $"{Environment.NewLine}{exception}";
	}

	sealed class NullScope : IDisposable
	{
		internal static NullScope Instance { get; } = new();

		public void Dispose() => GC.SuppressFinalize(this);
	}
}
