using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Norse.Hosting.Web.Server;

/// <summary>
/// The circuit's lifecycle net (spec §2.9): logs open/close/connection-down with a correlation id in
/// the platform's vocabulary, so a torn circuit is a traceable event, not a silent reconnect modal.
/// </summary>
sealed partial class LoggingCircuitHandler(ILogger<LoggingCircuitHandler> logger) : CircuitHandler
{
	public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
	{
		LogOpened(logger, circuit.Id);
		return Task.CompletedTask;
	}

	public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
	{
		LogConnectionDown(logger, circuit.Id);
		return Task.CompletedTask;
	}

	public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
	{
		LogClosed(logger, circuit.Id);
		return Task.CompletedTask;
	}

	[LoggerMessage(Level = LogLevel.Debug, Message = "Circuit {CircuitId} opened")]
	static partial void LogOpened(ILogger logger, string circuitId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Circuit {CircuitId} connection down")]
	static partial void LogConnectionDown(ILogger logger, string circuitId);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Circuit {CircuitId} closed")]
	static partial void LogClosed(ILogger logger, string circuitId);
}
