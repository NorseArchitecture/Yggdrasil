using System.Net;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

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
	async Task An_anonymous_browser_non_navigation_failing_a_policy_gets_a_bare_403()
	{
		using var host = await WebServerHost.StartAsync();
		var browser = host.CreateBrowserClient(); // follows no redirects, carries the anonymous cookie

		// /protected-probe requires IdentityPolicies.MaskedDisclosure, not .Self -- IdentityPolicies.Self is
		// RequireAuthenticatedUser(), and the anonymous browser principal genuinely IS authenticated (spec
		// §2.4), so a .Self-guarded probe would be satisfied by it (verified empirically: 200, not 403) and
		// could never exercise the forbid path this fact pins. MaskedDisclosure's RequireRole(SystemRole) is
		// the smallest policy the anonymous principal never holds. Ruling 44 (principal-at-the-door §2.4,
		// amended 2026-10-27) keeps the bare 403 only for a non-navigation, so the Accept is named
		// explicitly rather than left to the client's default.
		using var request = Get("/protected-probe", "application/json");
		var response = await browser.SendAsync(request, TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		response.Headers.Location.ShouldBeNull();
		(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
	}

	[Fact]
	async Task An_anonymous_browser_page_navigation_failing_a_policy_is_challenged_to_login()
	{
		using var host = await WebServerHost.StartAsync();
		var browser = host.CreateBrowserClient();

		// The same probe asked for as a page (GET + Accept text/html, Ruling 44): the anonymous principal is
		// sent to the identity cookie's login with the probe as its return URL, never a bare 403.
		using var request = Get("/protected-probe", HtmlAccept);
		var response = await browser.SendAsync(request, TestContext.Current.CancellationToken);

		ShouldBeLoginChallenge(response, "/protected-probe");
	}

	[Fact]
	async Task An_identity_principal_failing_a_policy_is_sent_to_access_denied_on_a_page_navigation()
	{
		using var host = await WebServerHost.StartAsync();
		var identity = host.CreateIdentityClient();

		// A signed-in user lacking a role is not helped by a login page, but a human on a page navigation is not
		// helped by a blank 403 either: Midgard forbids an identity principal's page navigation through the identity
		// cookie, which redirects to its access-denied path with the return URL kept (T3-3).
		using var request = Get("/protected-probe", HtmlAccept);
		var response = await identity.SendAsync(request, TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
		var location = response.Headers.Location.ShouldNotBeNull();
		var absolute = location.IsAbsoluteUri ? location : new Uri(new Uri("https://localhost"), location);
		absolute.AbsolutePath.ShouldBe("/Account/AccessDenied");
		QueryHelpers.ParseQuery(absolute.Query)["ReturnUrl"].ToString().ShouldBe("/protected-probe");
	}

	[Fact]
	async Task An_identity_principal_failing_a_policy_off_a_page_navigation_gets_a_bare_403()
	{
		using var host = await WebServerHost.StartAsync();
		var identity = host.CreateIdentityClient();

		// The access-denied redirect is confined to page navigations: the same principal asking as a machine gets "no".
		using var request = Get("/protected-probe", "application/json");
		var response = await identity.SendAsync(request, TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		response.Headers.Location.ShouldBeNull();
		(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
	}

	[Fact]
	async Task A_browser_principal_reaches_the_forgot_password_page_from_heimdalls_assembly()
	{
		using var host = await WebServerHost.StartAsync();
		var browser = host.CreateBrowserClient();

		// Train 1 (../Glitnir/docs/Himinbjorg/plans/2026-10-26-identity-account-lifecycle-train-1.md): the anonymous
		// lifecycle pages moved from Himinbjörg's scaffold to Heimdall. A stale Himinbjörg page on the same
		// template would have thrown on duplicate routes at startup; the Heimdall form's FormName in the body
		// proves the route resolves to Heimdall's component, not merely to something answering 200.
		var response = await browser.GetAsync(
			new Uri("/Account/ForgotPassword", UriKind.Relative), TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
			.ShouldContain("authn-forgot-password");
	}

	[Fact]
	async Task An_anonymous_browser_principal_is_forbidden_on_the_account_service()
	{
		using var host = await WebServerHost.StartAsync();
		var browser = host.CreateBrowserClient();
		// The first request mints the anonymous cookie; the second carries it, so the account gate sees an
		// authenticated anonymous principal, not none at all -- forbid, never challenge.
		await browser.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
		using var body = WebServerHost.EmptyGrpcBody();

		var response = await browser.PostAsync(
			new Uri("/grpc.account.v1.AccountService/GetAccountStatus", UriKind.Relative),
			body, TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
	}

	[Fact]
	async Task An_identity_principal_passes_the_account_gate()
	{
		using var host = await WebServerHost.StartAsync();
		var identity = host.CreateIdentityClient();
		using var body = WebServerHost.EmptyGrpcBody();

		var response = await identity.PostAsync(
			new Uri("/grpc.account.v1.AccountService/GetAccountStatus", UriKind.Relative),
			body, TestContext.Current.CancellationToken);
		// Trailers arrive only once the body has been read to its end.
		await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

		// Past the gate the handler runs against this host's unreachable database, so the call fails on
		// its own terms -- what this fact pins is that the failure is never the gate's: neither
		// PERMISSION_DENIED (7) nor UNAUTHENTICATED (16).
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		var status = response.TrailingHeaders.TryGetValues("grpc-status", out var values)
			? values.Single()
			: response.Headers.GetValues("grpc-status").Single();
		status.ShouldNotBe("7");
		status.ShouldNotBe("16");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	async Task An_anonymous_browser_principal_is_redirected_from_the_manage_tree_to_login(bool withAnonymousCookie)
	{
		using var host = await WebServerHost.StartAsync();
		var browser = host.CreateBrowserClient();
		// With the cookie, a first request mints it and the navigation carries it; without, the navigation
		// itself mints it on the same response that forbids it. Both are the anonymous principal.
		if (withAnonymousCookie)
			await browser.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

		// Ruling 44 (principal-at-the-door §2.4, amended 2026-10-27; NorseBrowserHandler.HandleForbiddenAsync):
		// an anonymous principal's page navigation that fails a policy is challenged to login, keeping the
		// return URL -- the redirect AuthorizeRouteView's RedirectToLogin could never issue, because the
		// endpoint's [Authorize] forbids in UseAuthorization before the router renders.
		using var request = Get("/Account/Manage", HtmlAccept);
		var response = await browser.SendAsync(request, TestContext.Current.CancellationToken);

		ShouldBeLoginChallenge(response, "/Account/Manage");
	}

	[Fact]
	async Task An_identity_principal_reaches_the_manage_shell_from_heimdalls_assembly()
	{
		using var host = await WebServerHost.StartAsync();
		var identity = host.CreateIdentityClient();

		// PersonalData needs no account read to render, so the body proves the composition alone: Heimdall's
		// ManageLayout ("Manage your account") nested in this host's app shell through IAppShellLayout,
		// around Heimdall's page ("Personal Data").
		var response = await identity.GetAsync(
			new Uri("/Account/Manage/PersonalData", UriKind.Relative), TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
		html.ShouldContain("Manage your account");
		html.ShouldContain("Personal Data");

		// The markup alone can't tell Heimdall's page from Himinbjorg's retired scaffold copy (both render
		// the same strings), so the routed component's own assembly is the proof.
		var endpoint = host.Services
			.GetRequiredService<EndpointDataSource>()
			.Endpoints
			.OfType<RouteEndpoint>()
			.Single(static endpoint => endpoint.RoutePattern.RawText == "/Account/Manage/PersonalData");
		endpoint.Metadata.GetRequiredMetadata<ComponentTypeMetadata>().Type.Assembly.GetName().Name
			.ShouldBe("Norse.AuthN.Components.FluentUI");
	}

	/// <summary>The Accept a browser sends on a top-level page navigation.</summary>
	const string HtmlAccept = "text/html,application/xhtml+xml,*/*;q=0.8";

	/// <summary>A GET for <paramref name="path" /> naming exactly <paramref name="accept" />.</summary>
	static HttpRequestMessage Get(string path, string accept)
	{
		HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
		request.Headers.Accept.ParseAdd(accept);
		return request;
	}

	/// <summary>Asserts a 302 to exactly <c>/Account/Login</c> whose <c>ReturnUrl</c> is exactly <paramref name="returnUrl" />.</summary>
	static void ShouldBeLoginChallenge(HttpResponseMessage response, string returnUrl)
	{
		response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
		var location = response.Headers.Location.ShouldNotBeNull();
		var absolute = location.IsAbsoluteUri ? location : new Uri(new Uri("https://localhost"), location);
		absolute.AbsolutePath.ShouldBe("/Account/Login");
		QueryHelpers.ParseQuery(absolute.Query)["ReturnUrl"].ToString().ShouldBe(returnUrl);
	}
}
