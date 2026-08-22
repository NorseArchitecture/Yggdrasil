using Microsoft.AspNetCore.Components.Server.Circuits;
using Norse.Abstractions.Web.Server.Mediator;

namespace Norse.Hosting.Web.Server.Tests.Authentication.CircuitDiagnostics;

/// <summary>
///     Captures this circuit's own scoped <see cref="IPrincipalAccessor" /> into
///     <see cref="CircuitPrincipalCaptureRegistry" /> the moment the circuit opens. Registered as an
///     <b>additional</b> scoped <see cref="CircuitHandler" /> -- production's own <c>LoggingCircuitHandler</c>
///     (<c>src/Hosting.Web.Server/Program.cs</c>) keeps running unmodified; ASP.NET Core resolves and invokes
///     every registered <c>CircuitHandler</c>, so adding a second one via DI is additive, not a replacement.
/// </summary>
sealed class CircuitPrincipalCaptureHandler(
	IPrincipalAccessor principalAccessor,
	CircuitPrincipalCaptureRegistry registry) : CircuitHandler
{
	public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
	{
		registry.Register(circuit.Id, principalAccessor);
		return base.OnCircuitOpenedAsync(circuit, cancellationToken);
	}
}
