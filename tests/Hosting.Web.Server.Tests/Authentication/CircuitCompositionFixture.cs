using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Norse.Abstractions.Web.Server.Mediator;
using Norse.Hosting.BrowserTesting;
using Norse.Hosting.Web.Server.Tests.Authentication.CircuitDiagnostics;
using Norse.Infrastructure.Web.Server.Authentication;

namespace Norse.Hosting.Web.Server.Tests.Authentication;

/// <summary>
///     Task 14 fact 4's real-Kestrel host (see <see cref="LaneCompositionTests" />): a
///     <see cref="BrowserHostFixture{TEntryPoint}" /> over the real <c>Program.cs</c> composition root,
///     extended -- entirely from this test project, via <c>ConfigureTestServices</c> -- with
///     <see cref="CircuitPrincipalCaptureHandler" />, an additional <see cref="CircuitHandler" /> that
///     captures a live circuit's own scoped <see cref="IPrincipalAccessor" /> into
///     <see cref="CircuitPrincipalCaptureRegistry" />. Facts 1-3's <see cref="WebServerHost" /> stays
///     in-memory (<see cref="Microsoft.AspNetCore.TestHost.TestServer" />) and is untouched by this type;
///     fact 4 needs a real listening socket for Playwright to connect to, which only a real-Kestrel host
///     (<see cref="BrowserHostFixture{TEntryPoint}" />'s pattern) provides.
///     <para>
///         <b>Design note (why no second <c>MapRazorComponents</c> root):</b> an earlier version of this
///         fixture mapped a dedicated test-only diagnostic page via a second
///         <c>MapRazorComponents&lt;T&gt;().AddInteractiveServerRenderMode()</c> call. That call maps its own
///         private circuit hub at the framework's fixed <c>/_blazor</c> path -- there is no overload or
///         option to choose a different path (<c>ServerComponentsEndpointOptions</c> carries no such
///         property) -- so two such calls in one app collide: every <c>/_blazor/negotiate</c> request throws
///         <c>AmbiguousMatchException</c>. This is a confirmed, documented ASP.NET Core limitation, not a
///         local mistake (Microsoft's own Blazor SignalR guidance documents the identical collision for
///         <c>MapBlazorHub</c> combined with <c>AddInteractiveServerRenderMode</c>, citing open
///         <c>dotnet/aspnetcore</c> issues #51698, #52156, #63520 with no supported workaround for two
///         independent interactive-server endpoint sets in one app). This <see cref="CircuitHandler" />-based
///         design sidesteps the wall entirely: it rides an <b>existing</b>, already-interactive production
///         page (<see cref="DiagnosticTargetPath" />) and needs no second hub, no second root, and no JS
///         interop at all.
///     </para>
/// </summary>
sealed class CircuitCompositionFixture : BrowserHostFixture<Program>
{
	/// <summary>
	///     An existing, dependency-free production interactive page (<c>Hosting.Web.Components/Pages/Counter.razor</c>,
	///     <c>@page "/counter"</c>) reused as both the handshake target and the circuit-connect target --
	///     no test-only route is mapped for this fact (see the design note above).
	/// </summary>
	internal const string DiagnosticTargetPath = "/counter";

	static readonly TimeSpan _circuitRegistrationPoll = TimeSpan.FromMilliseconds(50);

	protected override void ConfigureWebHost(IWebHostBuilder builder) =>
		builder.ConfigureTestServices(services =>
		{
			services.AddSingleton<CircuitPrincipalCaptureRegistry>();
			services.AddScoped<CircuitHandler, CircuitPrincipalCaptureHandler>();
		});

	/// <summary>
	///     The handshake: an ordinary HTTP GET against the real Kestrel instance -- the only kind of request
	///     that can carry a <c>Set-Cookie</c> (design §2.5) -- against the same page the browser will later
	///     connect a circuit to. Reads the minted GUID back by decoding the cookie through the host's own
	///     <see cref="IDataProtectionProvider" />, the same key ring <c>NorseAnonymousHandler</c> (Midgard)
	///     protected it with, since this is the same running host instance, not a separate process with a
	///     separate key ring.
	/// </summary>
	internal async Task<AnonymousHandshake> HandshakeAsync(CancellationToken cancellationToken)
	{
		var services = await GetServicesAsync();
		using HttpClient client = new() { BaseAddress = Origin };
		var response = await client.GetAsync(
			new Uri(DiagnosticTargetPath, UriKind.Relative), cancellationToken);
		response.EnsureSuccessStatusCode();

		if (!response.Headers.TryGetValues("Set-Cookie", out var rawHeaders))
			throw new InvalidOperationException("The handshake response carried no Set-Cookie header.");

		var anonymousCookie = SetCookieHeaderValue.ParseList([.. rawHeaders])
			.FirstOrDefault(cookie => cookie.Name.Equals("Norse.Anonymous", StringComparison.Ordinal)) ??
			throw new InvalidOperationException("The handshake response carried no Norse.Anonymous cookie.");
		var cookieValue = anonymousCookie.Value.ToString();

		var protector = services.GetRequiredService<IDataProtectionProvider>()
			.CreateProtector(NorseAnonymousOptions.ProtectionPurpose);
		var id = Guid.Parse(protector.Unprotect(cookieValue));

		return new(id, cookieValue);
	}

	/// <summary>
	///     Polls <see cref="CircuitPrincipalCaptureRegistry" /> until <see cref="CircuitPrincipalCaptureHandler" />
	///     has captured a live circuit's own scoped <see cref="IPrincipalAccessor" /> (fires from
	///     <c>OnCircuitOpenedAsync</c>, shortly after the browser's SignalR connection completes) or
	///     <paramref name="cancellationToken" /> ends the wait.
	/// </summary>
	internal async Task<IPrincipalAccessor> WaitForCircuitPrincipalAccessorAsync(CancellationToken cancellationToken)
	{
		var services = await GetServicesAsync();
		var registry = services.GetRequiredService<CircuitPrincipalCaptureRegistry>();
		while (true)
		{
			if (registry.TryGetAny() is { } accessor)
				return accessor;
			await Task.Delay(_circuitRegistrationPoll, cancellationToken);
		}
	}
}

/// <summary>The handshake's outcome: the minted GUID, plus the exact raw cookie value that carries it.</summary>
sealed record AnonymousHandshake(Guid Id, string CookieValue);
