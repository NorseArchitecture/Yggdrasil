using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Norse.Abstractions.Components.Authorization;
using Norse.Abstractions.Web.Server.Mediator;
using Norse.AuthN.Services;
using Norse.Hosting.BrowserTesting;
using Norse.Hosting.Web.Server.Tests.BrowserRuntime;
using Norse.Reference;

namespace Norse.Hosting.Web.Server.Tests.Authentication;

/// <summary>
///     Wired-not-designed composition assertions for the principal-at-the-door lane composition (Glitnir
///     Platform/specs/2026-08-21-principal-at-the-door-design.md §2.2, §2.4, §2.5, §7), Task 14. Every
///     fact boots the real <c>Program.cs</c> composition root -- facts 1-3 via <see cref="WebServerHost" />,
///     fact 4 via <see cref="CircuitCompositionFixture" /> (a real-Kestrel host fact 4 alone needs, to give
///     Playwright a real socket to connect to) -- never a hand-rolled stand-in, so every assertion here
///     fails if the registration it checks is ever deleted from <c>Program.cs</c>.
/// </summary>
public sealed class LaneCompositionTests
{
	[Fact]
	async Task Every_previously_hand_registered_policy_is_still_registered()
	{
		using var host = await WebServerHost.StartAsync();
		var provider = host.Services.GetRequiredService<IAuthorizationPolicyProvider>();

		foreach (var name in new[]
			{
				AuthNPolicies.Public, ReferencePolicies.Public,
				IdentityPolicies.Self, IdentityPolicies.MaskedDisclosure,
				NorsePolicies.Probe
			})
		{
			(await provider.GetPolicyAsync(name)).ShouldNotBeNull($"policy '{name}' was not registered");
		}
	}

	[Fact]
	async Task A_browser_request_to_the_root_receives_an_anonymous_principal_and_cookie()
	{
		using var host = await WebServerHost.StartAsync();

		var response = await host.Client.GetAsync(
			new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

		response.Headers.GetValues("Set-Cookie")
			.ShouldContain(h => h.StartsWith("Norse.Anonymous=", StringComparison.Ordinal));
	}

	[Fact]
	async Task The_reference_facade_is_closed_to_a_credentialless_caller()
	{
		using var host = await WebServerHost.StartAsync();

		var response = await host.Client.GetAsync(
			new Uri("/api/reference/countries/US", UriKind.Relative), TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
		response.Headers.TryGetValues("Set-Cookie", out _).ShouldBeFalse();
	}

	// Needs a real listening Kestrel instance and a real browser: a live Blazor Server circuit (the
	// SignalR /_blazor hub) has no shape an in-memory TestServer/raw HubConnection can drive without
	// reverse-engineering the private wire protocol (decision recorded in the Task 14 fact 4 brief).
	// CircuitCompositionFixture boots the real Program.cs composition root on a real socket. It reads the
	// live circuit's principal server-side, through CircuitPrincipalCaptureHandler (a second, additional
	// CircuitHandler) rather than a second MapRazorComponents root or JS interop -- see
	// CircuitCompositionFixture's doc comment for why: a second interactive-server root collides on the
	// framework's single, unconfigurable /_blazor hub path (a confirmed ASP.NET Core limitation, not a
	// local mistake). Nothing here touches production Program.cs or Hosting.Web.Components.
	[Fact(Explicit = true, Timeout = 300_000)]
	async Task A_circuit_inherits_the_handshake_identity_and_concurrency_mints_no_second_guid()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using CircuitCompositionFixture fixture = new();

		// The handshake: an ordinary HTTP GET against the real Kestrel instance -- the only kind of request
		// in a circuit's life that can carry a Set-Cookie (design §2.5) -- and therefore the only place a
		// mint can happen.
		var handshake = await fixture.HandshakeAsync(cancellationToken);

		var evidence = await fixture.OpenEvidenceAsync(
			nameof(A_circuit_inherits_the_handshake_identity_and_concurrency_mints_no_second_guid));

		// The browser carries the exact cookie the handshake minted, injected into the context before any
		// navigation -- it must never get the chance to mint a second, separate identity of its own.
		await evidence.AddCookiesAsync(
		[
			new()
			{
				Name = "Norse.Anonymous",
				Value = handshake.CookieValue,
				Domain = fixture.Origin.Host,
				Path = "/",
				HttpOnly = true,
				Secure = true,
				SameSite = SameSiteAttribute.Lax,
			},
		]);

		List<string> setCookieHeadersAfterHandshake = [];

		await evidence.ExecuteAsync(async operation =>
		{
			var page = operation.Page;

			void RecordSetCookie(object? _, IResponse response)
			{
				if (response.Headers.TryGetValue("set-cookie", out var value))
					setCookieHeadersAfterHandshake.Add(value);
			}

			page.Response += RecordSetCookie;
			try
			{
				IPrincipalAccessor? circuitPrincipalAccessor = null;
				await operation.RunPhaseAsync(
					"circuit connect",
					BrowserTimeouts.HostStartup,
					async phaseCancellationToken =>
					{
						await page.GotoAsync(CircuitCompositionFixture.DiagnosticTargetPath, new()
						{
							Timeout = BrowserTimeouts.PlaywrightHostStartupMilliseconds,
						}).WaitAsync(phaseCancellationToken);

						// The "connect" step: wait for CircuitPrincipalCaptureHandler.OnCircuitOpenedAsync to
						// fire server-side and capture this circuit's own scoped IPrincipalAccessor.
						circuitPrincipalAccessor =
							await fixture.WaitForCircuitPrincipalAccessorAsync(phaseCancellationToken);
					});

				Guid[] observed = [];
				await operation.RunPhaseAsync(
					"twenty concurrent circuit reads",
					BrowserTimeouts.BrowserOperation,
					async phaseCancellationToken =>
					{
						// Twenty concurrent operations against the one circuit's own scoped
						// IPrincipalAccessor. If anything mid-circuit could mint, this is where a second GUID
						// would appear -- and it must not, because the circuit has no response left to write
						// a cookie on (design §2.5).
						var principals = await Task.WhenAll(Enumerable.Range(0, 20)
							.Select(_ => circuitPrincipalAccessor!.GetPrincipalAsync(phaseCancellationToken).AsTask()));
						observed = [.. principals.Select(static principal =>
							Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
								throw new InvalidOperationException(
									"The circuit's principal carries no ClaimTypes.NameIdentifier claim.")))];
					});

				observed.ShouldAllBe(id => id == handshake.Id);
			}
			finally
			{
				page.Response -= RecordSetCookie;
			}
			// Same evidence policy as WebServerBrowserRuntimeSmokeTests: this is the identical composition
			// root, so the same bounded FluentUI rc5 startup allowances (overflowchange/accordionchange,
			// Web.Server CLAUDE.md) and the same once-only /_blazor/disconnect abort-on-teardown allowance
			// apply here too -- not a second, parallel policy to keep in sync by hand.
		}, WebServerBrowserFixture.CreateEvidencePolicy());

		setCookieHeadersAfterHandshake.ShouldBeEmpty();
	}
}
