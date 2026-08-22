using System.Collections.Concurrent;
using Norse.Abstractions.Web.Server.Mediator;

namespace Norse.Hosting.Web.Server.Tests.Authentication.CircuitDiagnostics;

/// <summary>
///     Task 14 fact 4's test-only diagnostic surface -- see the Task 14 fact 4 brief and the design
///     (Glitnir Platform/specs/2026-08-21-principal-at-the-door-design.md §2.5, §7). A singleton
///     <see cref="CircuitPrincipalCaptureHandler" /> (a <see cref="Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler" />,
///     added alongside production's own via DI -- <c>CircuitHandler</c> registrations are additive, never a
///     replacement) populates this the moment each circuit it observes opens, with that circuit's own scoped
///     <see cref="IPrincipalAccessor" />. Lets the test reach a live, browser-established circuit's principal
///     directly from the same process -- no second <c>MapRazorComponents</c> root, no JS interop round-trip,
///     and (per the design) still a genuine proof: <see cref="IPrincipalAccessor" /> is the exact object
///     under test, and this is the exact scoped instance the live circuit's own components resolve.
///     <para>
///         Registered entirely from the test project (<see cref="CircuitCompositionFixture" />'s
///         <c>ConfigureTestServices</c>) -- nothing added to production <c>Program.cs</c> or
///         <c>Hosting.Web.Components</c>.
///     </para>
/// </summary>
sealed class CircuitPrincipalCaptureRegistry
{
	readonly ConcurrentDictionary<string, IPrincipalAccessor> _byCircuitId = new();

	internal void Register(string circuitId, IPrincipalAccessor accessor) =>
		_byCircuitId[circuitId] = accessor;

	/// <summary>
	///     The first (only, in this fact's single-circuit scenario) captured accessor, or <see langword="null" />
	///     if no circuit has opened yet -- the shape a poll loop needs while waiting for
	///     <see cref="Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler.OnCircuitOpenedAsync" />
	///     to fire.
	/// </summary>
	internal IPrincipalAccessor? TryGetAny() => _byCircuitId.Values.FirstOrDefault();
}
