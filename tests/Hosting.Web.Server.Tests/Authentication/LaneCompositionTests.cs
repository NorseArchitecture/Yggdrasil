using System.Collections.Concurrent;
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

	// The bug this pins (browser fix, ../Glitnir/docs/Platform/plans/2026-08-21-principal-at-the-door.md
	// authn-uniformity amendment): Mímir's CountryLookup component calls IReferenceService.GetCountry over
	// gRPC-Web from the same page a browser-lane request already minted an anonymous cookie on. Before the
	// fix, NorseSchemes.IdentityCookieOnly forwarded straight to the real identity cookie with no fallback,
	// so that anonymous visitor's own gRPC-Web call failed authentication and the lookup broke -- even
	// though ReferencePolicies.Public is satisfied by any principal, anonymous role included. Paired with
	// ChallengeAndForbidTests.The_grpc_lane_challenges_without_a_redirect (no cookie at all -> bare 401):
	// together they pin both edges of NorseGrpcHandler's read-only anonymous fallback.
	[Fact]
	async Task The_grpc_lane_accepts_an_already_established_anonymous_cookie()
	{
		using var host = await WebServerHost.StartAsync();
		var browser = host.CreateBrowserClient();

		// Mints the anonymous cookie exactly as a first page view would; persisted on this client's own
		// cookie jar (WebApplicationFactoryClientOptions.HandleCookies default) so the gRPC-Web call below
		// carries it the same way a browser tab's next fetch would.
		await browser.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

		using var body = WebServerHost.EmptyGrpcBody();
		var response = await browser.PostAsync(
			new Uri("/grpc.reference.v1.ReferenceService/GetCountry", UriKind.Relative),
			body, TestContext.Current.CancellationToken);

		// Not a bare 401/403 -- authentication succeeded and the request reached the gRPC pipeline, which
		// always answers HTTP 200 with the business (or business-fault) result riding grpc-status/the
		// Outcome<T> payload, never the transport status. The empty-body request's own business outcome is
		// out of scope here; CountryLookupE2ETests already proves the real round trip against a live database.
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
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

		// The browser carries the exact cookie the handshake minted, seeded into the context before any
		// navigation -- it must never get the chance to mint a second, separate identity of its own.
		var evidence = await fixture.OpenEvidenceAsync(
			nameof(A_circuit_inherits_the_handshake_identity_and_concurrency_mints_no_second_guid),
			cookies:
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

		// IResponse.Headers is Playwright's synchronous, best-effort header snapshot, which deliberately
		// excludes security-related headers -- Set-Cookie included, per Playwright's own docs. Only the
		// async HeadersArrayAsync()/AllHeadersAsync() surface those, and both need the response's own
		// page/context still open -- so every fetch is started the moment the response is observed, not
		// deferred until after ExecuteAsync's evidence cleanup has already closed the page.
		ConcurrentQueue<Task<IReadOnlyList<Header>>> headerFetchesAfterHandshake = new();

		await evidence.ExecuteAsync(async operation =>
		{
			var page = operation.Page;

			void RecordResponse(object? _, IResponse response) =>
				headerFetchesAfterHandshake.Enqueue(response.HeadersArrayAsync());

			page.Response += RecordResponse;
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

				// Awaited here, still inside ExecuteAsync's scope -- once the callback returns, evidence
				// cleanup closes the page and every pending HeadersArrayAsync() call would fail with
				// TargetClosedException instead of returning data.
				var headerBatches = await Task.WhenAll(headerFetchesAfterHandshake);

				// NorseAnonymousHandler.ReadOrMint's existing-cookie branch reissues Set-Cookie on a sliding
				// lifetime, so real Set-Cookie traffic exists on this navigation -- the invariant under test
				// is not "no Set-Cookie ever" (the page also mints its own, unrelated
				// .AspNetCore.Antiforgery.* cookie, which this must not choke on), it's "no Norse.Anonymous
				// Set-Cookie carrying a different identity than the handshake minted". Scoped to that one
				// cookie's own name=value pair, not the whole Set-Cookie header (which trails
				// path/samesite/httponly attributes a raw StartsWith would also have to anticipate).
				var anonymousCookieValuesAfterHandshake = headerBatches
					.SelectMany(static headers => headers)
					.Where(static header => string.Equals(header.Name, "set-cookie", StringComparison.OrdinalIgnoreCase))
					.Select(static header => header.Value.Split(';', 2)[0])
					.Where(static nameValue => nameValue.StartsWith("Norse.Anonymous=", StringComparison.Ordinal))
					.ToArray();

				anonymousCookieValuesAfterHandshake.ShouldAllBe(nameValue =>
					nameValue == $"Norse.Anonymous={handshake.CookieValue}");
			}
			finally
			{
				page.Response -= RecordResponse;
			}
			// Same evidence policy as WebServerBrowserRuntimeSmokeTests: this is the identical composition
			// root, so the same bounded FluentUI rc5 startup allowances (overflowchange/accordionchange,
			// Web.Server CLAUDE.md) and the same once-only /_blazor/disconnect abort-on-teardown allowance
			// apply here too -- not a second, parallel policy to keep in sync by hand.
		}, WebServerBrowserFixture.CreateEvidencePolicy());
	}
}
