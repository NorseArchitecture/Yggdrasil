using System.Net;

namespace Norse.Hosting.Web.Server.Tests.Authentication;

/// <summary>
///     Task 15 (principal-at-the-door, ../Glitnir/docs/Platform/plans/2026-08-21-principal-at-the-door.md):
///     pins challenge and forbid semantics per lane at the composed host. Fixing the two <c>Outcome&lt;T&gt;</c>
///     folds does not control responses the framework generates -- challenge and forbid are per-handler
///     operations, so each lane's behavior was decided where the lane lives (Midgard's Tasks 6 and 7,
///     already shipped, v0.0.31). Each fact is exercised separately: each lane has its own handler and they
///     fail independently.
/// </summary>
public sealed class ChallengeAndForbidTests
{
	[Fact]
	async Task The_rest_lane_challenges_with_a_bare_401()
	{
		using var host = await WebServerHost.StartAsync();

		var response = await host.Client.GetAsync(
			new Uri("/api/reference/countries/US", UriKind.Relative), TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
		response.Headers.Location.ShouldBeNull();
	}

	[Fact]
	async Task The_grpc_lane_challenges_without_a_redirect()
	{
		using var host = await WebServerHost.StartAsync();
		using var body = WebServerHost.EmptyGrpcBody();

		// The plan's own illustrative path names "norse.Reference.IReferenceService" -- the C# interface's
		// namespace/name, not the wire service name. IReferenceService's [ServiceContract] actually names
		// "grpc.reference.v1.ReferenceService" (Mimir/src/Reference.Contracts/IReferenceService.cs); the
		// wrong path was verified empirically to miss routing entirely and land on grpc-dotnet's own
		// UNIMPLEMENTED fallback (a 200 with a grpc-status trailer, logged as "Service ... is unimplemented")
		// -- which also carries no Location header and an empty body, so it passed this fact without ever
		// exercising NorseSchemes.IdentityCookieOnly's forward to the machine lane at all. The corrected path
		// below reaches real authorization: gRPC controllers are machine-lane by inheritance
		// (GrpcControllerBase), so a bearer-less call is challenged on NorseSchemes.Machine, which is
		// registered as an AddPolicyScheme forward -- not a hand-rolled handler -- onto OpenIddict's own
		// validation scheme (OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
		// AuthenticationBuilderExtensions.AddNorseAuthentication, NorseSchemes.Machine's own doc comment).
		// The forwarded-to OpenIddict validation handler is what actually issues the 401 challenge.
		var response = await host.Client.PostAsync(
			new Uri("/grpc.reference.v1.ReferenceService/GetCountry", UriKind.Relative),
			body, TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		response.Headers.Location.ShouldBeNull();
		(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
	}

	[Fact]
	async Task An_anonymous_browser_principal_failing_a_policy_gets_403_not_a_login_redirect()
	{
		using var host = await WebServerHost.StartAsync();
		var browser = host.CreateBrowserClient(); // follows no redirects, carries the anonymous cookie

		// /protected-probe requires IdentityPolicies.MaskedDisclosure, not .Self -- IdentityPolicies.Self is
		// RequireAuthenticatedUser(), and the anonymous browser principal genuinely IS authenticated (spec
		// §2.4), so a .Self-guarded probe would be satisfied by it (verified empirically: 200, not 403) and
		// could never exercise the forbid path this fact pins. MaskedDisclosure's RequireRole(SystemRole) is
		// the smallest policy the anonymous principal never holds.
		var response = await browser.GetAsync(
			new Uri("/protected-probe", UriKind.Relative), TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		response.Headers.Location.ShouldBeNull();
		(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
	}
}
