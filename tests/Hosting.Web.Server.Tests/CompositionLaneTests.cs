using System.Buffers.Text;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Norse.Abstractions.Components.Authorization;
using Norse.Abstractions.Contracts;
using Norse.AuthN.Services;
using Norse.Hosting.Web.Server.Tests.Authentication;
using Norse.Identity.EntityFramework;
using Norse.Primitives;
using Norse.Reference;
using OpenIddict.Abstractions;

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
///     <see cref="CompositionTests" />' lane facts (Train 3, Task 6.1): the real <c>Program.cs</c> booted against a
///     migrated <c>norse_identity</c> (<see cref="WebServerHost.StartAsync(MachineAuthPostgresFixture, Action{Microsoft.AspNetCore.Hosting.IWebHostBuilder})" />, T14), driven
///     over the wire with a cookie jar standing in for the browser's. Every fact rides a gRPC channel; the two-factor
///     lifecycle, the passkey management lane and the anonymous sign-in methods also ride gRPC-Web through
///     <see cref="Norse.Infrastructure.Web.Client.Grpc.GrpcWebCallInvoker" />, exactly as the WASM client composes it
///     (<see cref="WireLane.GrpcWeb" />). The stamp validator runs on every
///     request there, so each fact that rotates the security stamp proves the rotation where a browser feels it: the
///     cookie from before the step is refused on its next call, and the refreshed one passes.
/// </summary>
/// <remarks>
///     A separate class from <see cref="CompositionTests" /> because it needs the Postgres collection: every fact
///     here shares <see cref="MachineAuthPostgresFixture" />'s database and key root, never a second one (EF's cached
///     identity model closes over the first host's protectors). Each fact seeds its own account.
/// </remarks>
/// <param name="postgres">The migrated identity and reference databases.</param>
[Collection("MachineAuthPostgres")]
public sealed class CompositionLaneTests(MachineAuthPostgresFixture postgres)
{
	const string Password = "Correct-Horse-9";

	/// <summary>The identity cookie's name (Himinbjorg's <c>AddNorseIdentity</c>).</summary>
	const string IdentityCookie = "Norse.Identity";

	static CancellationToken Cancellation => TestContext.Current.CancellationToken;

	[Fact]
	async Task A_local_return_url_survives_login()
	{
		using var host = await WebServerHost.StartAsync(postgres);
		var email = await SeedAsync(host, "return-url");
		using var session = await AnonymousAsync(host);

		var login = await session.Service<IAuthenticationService>().Login(
			new() { EmailInput = email, Password = Password, ReturnUrl = "/counter?tab=2" }, Cancellation);

		// T3-2: the server resolves the landing, and a local one survives whole, query included.
		Ok(login).NextUrl.ShouldBe("/counter?tab=2");
		Ok(await StatusAsync(session)).HasPassword.ShouldBeTrue();
	}

	// End state rows 21-24, in order, on one account.
	[Theory]
	[InlineData(WireLane.Grpc)]
	[InlineData(WireLane.GrpcWeb)]
	async Task The_two_factor_lifecycle_runs_over_the_wire(WireLane lane)
	{
		using var host = await WebServerHost.StartAsync(postgres);
		var email = await SeedAsync(host, "lifecycle");
		using var session = await SignInAsync(host, email, lane);
		var twoFactor = session.Service<ITwoFactorService>();

		// Row 21: setup, then enable with a code computed from the shown key. The first enable issues ten codes that
		// ride a one-shot handoff, revealed once.
		var key = Ok(await twoFactor.GetAuthenticatorSetup(Cancellation)).SharedKey;
		var before = await SnapshotAsync(host, session, email);
		var enabled = Ok(await twoFactor.EnableAuthenticator(new() { Code = TotpCode.Now(Raw(key)) }, Cancellation));
		await ShouldRotateAsync(host, session, email, before);
		Uri handoffUrl = new(WireSession.Origin, enabled.NextUrl);
		handoffUrl.AbsolutePath.ShouldBe("/Account/Manage/ShowRecoveryCodes");
		var handoff = QueryHelpers.ParseQuery(handoffUrl.Query)["handoff"].ToString();
		var issued = Ok(await twoFactor.RevealRecoveryCodes(new() { Handoff = handoff }, Cancellation)).RecoveryCodes;
		issued.Length.ShouldBe(10);
		var shownAgain = Fails(await twoFactor.RevealRecoveryCodes(new() { Handoff = handoff }, Cancellation));
		shownAgain.Category.ShouldBe(ErrorCategory.Validation);
		Message(shownAgain).ShouldBe("These recovery codes have already been shown.");

		// Row 22: generate answers in place and rotates nothing.
		before = await SnapshotAsync(host, session, email);
		var generated = Ok(await twoFactor.GenerateRecoveryCodes(Cancellation)).RecoveryCodes;
		generated.Length.ShouldBe(10);
		generated.Intersect(issued).ShouldBeEmpty();
		await ShouldNotRotateAsync(host, email, before);

		// Row 23: reset disables two-factor and replaces the key; setup then shows the replacement, and re-enabling
		// with a fresh code from it keeps the remaining codes, so it lands verified rather than on a new handoff.
		before = await SnapshotAsync(host, session, email);
		Ok(await twoFactor.ResetAuthenticator(Cancellation)).NextUrl
			.ShouldBe("/Account/Manage/EnableAuthenticator?status=authenticator-reset");
		await ShouldRotateAsync(host, session, email, before);
		Ok(await StatusAsync(session)).TwoFactorEnabled.ShouldBeFalse();
		var replacement = Ok(await twoFactor.GetAuthenticatorSetup(Cancellation)).SharedKey;
		replacement.ShouldNotBe(key);
		before = await SnapshotAsync(host, session, email);
		Ok(await twoFactor.EnableAuthenticator(new() { Code = TotpCode.Now(Raw(replacement)) }, Cancellation)).NextUrl
			.ShouldBe("/Account/Manage/TwoFactorAuthentication?status=authenticator-verified");
		await ShouldRotateAsync(host, session, email, before);
		Ok(await StatusAsync(session)).TwoFactorEnabled.ShouldBeTrue();

		// Row 24: disable; a second disable is refused and changes nothing.
		before = await SnapshotAsync(host, session, email);
		Ok(await twoFactor.DisableTwoFactor(Cancellation)).NextUrl
			.ShouldBe("/Account/Manage/TwoFactorAuthentication?status=two-factor-disabled");
		await ShouldRotateAsync(host, session, email, before);
		before = await SnapshotAsync(host, session, email);
		var disabledAgain = Fails(await twoFactor.DisableTwoFactor(Cancellation));
		disabledAgain.Category.ShouldBe(ErrorCategory.Validation);
		Message(disabledAgain).ShouldBe("Two-factor authentication is not enabled.");
		await ShouldNotRotateAsync(host, email, before);
	}

	[Fact]
	async Task Forget_this_browser_over_the_wire()
	{
		using var host = await WebServerHost.StartAsync(postgres);
		var email = await SeedAsync(host, "forget");
		using var session = await SignInAsync(host, email);
		var key = await EnableTwoFactorAsync(session);
		await SignInRememberingAsync(session, email, key);
		Ok(await StatusAsync(session)).IsMachineRemembered.ShouldBeTrue();

		Ok(await session.Service<ITwoFactorService>().ForgetTwoFactorClient(Cancellation)).NextUrl
			.ShouldBe("/Account/Manage/TwoFactorAuthentication?status=browser-forgotten");

		// The browser is forgotten on the response itself: the next password sign-in asks for the second factor again.
		Ok(await StatusAsync(session)).IsMachineRemembered.ShouldBeFalse();
		Ok(await LoginAsync(session, email, Password)).NextUrl.ShouldStartWith("/Account/LoginWith2fa?challenge=");
	}

	// Characterization, not a red-first fact: remember-this-machine shipped in Train 1 and already works; this pins
	// that it survives Train 3's composition over the wire.
	[Fact]
	async Task Remember_machine_round_trips_over_the_wire()
	{
		using var host = await WebServerHost.StartAsync(postgres);
		var email = await SeedAsync(host, "remember");
		using var session = await SignInAsync(host, email);
		var key = await EnableTwoFactorAsync(session);
		await SignInRememberingAsync(session, email, key);

		// The remembered browser signs in on the password alone; a browser without the remember cookie is challenged.
		Ok(await LoginAsync(session, email, Password)).NextUrl.ShouldBe("/");
		using var stranger = await AnonymousAsync(host);
		Ok(await LoginAsync(stranger, email, Password)).NextUrl.ShouldStartWith("/Account/LoginWith2fa?challenge=");
	}

	// Characterization, not a red-first fact: lockout on failure shipped with Train 1's login and already works; this
	// pins it over the composed wire. Identity's default locks out on the fifth failure.
	[Fact]
	async Task Login_lockout_over_the_wire()
	{
		using var host = await WebServerHost.StartAsync(postgres);
		var email = await SeedAsync(host, "lockout");
		using var session = await AnonymousAsync(host);

		// A wrong password crosses the wire as gRPC UNAUTHENTICATED, which the client decodes to Unauthorized: the
		// silence ruling collapses InvalidCredentials into it (Midgard's RpcExceptionExtensions).
		for (var attempt = 1; attempt < 5; attempt++)
			Fails(await LoginAsync(session, email, "Wrong-Horse-9")).Category.ShouldBe(ErrorCategory.Unauthorized);
		Fails(await LoginAsync(session, email, "Wrong-Horse-9")).Category.ShouldBe(ErrorCategory.LockedOut);

		// Locked out, the right password fails too, and says so.
		var locked = Fails(await LoginAsync(session, email, Password));
		locked.Category.ShouldBe(ErrorCategory.LockedOut);
		Message(locked).ShouldBe("This account is locked out. Try again later or reset your password.");
		session.Cookie(IdentityCookie).ShouldBeNull();
	}

	[Theory]
	[InlineData(WireLane.Grpc)]
	[InlineData(WireLane.GrpcWeb)]
	async Task The_passkey_management_lane_resolves(WireLane lane)
	{
		using var host = await WebServerHost.StartAsync(postgres);
		var email = await SeedAsync(host, "passkeys");
		using var session = await SignInAsync(host, email, lane);
		var passkeys = session.Service<IPasskeyService>();

		Ok(await passkeys.GetPasskeys(Cancellation)).Passkeys.ShouldBeEmpty();
		// T3-33: user verification is required in the options the browser gets.
		Ok(await passkeys.GetPasskeyCreationOptions(Cancellation)).OptionsJson.ShouldContain("\"userVerification\":\"required\"");

		// Well-formed but unknown: the id decodes, and no passkey of the caller's carries it.
		var unknown = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
		Fails(await passkeys.RenamePasskey(new() { CredentialId = unknown, Name = "Laptop" }, Cancellation)).Category
			.ShouldBe(ErrorCategory.NotFound);
		Fails(await passkeys.RemovePasskey(new() { CredentialId = unknown }, Cancellation)).Category
			.ShouldBe(ErrorCategory.NotFound);
	}

	[Theory]
	[InlineData(WireLane.Grpc)]
	[InlineData(WireLane.GrpcWeb)]
	async Task Sign_in_methods_resolve_anonymously(WireLane lane)
	{
		using var host = await WebServerHost.StartAsync(postgres);
		using var session = await AnonymousAsync(host, lane);
		var methods = session.Service<ISignInMethodsService>();

		// T3-38: the server authors every link between the sign-in pages, each carrying only the resolved landing.
		var local = Ok(await methods.GetSignInMethods(new() { ReturnUrl = "/counter" }, Cancellation));
		local.PasswordSignInUrl.ShouldBe("/Account/Login?returnUrl=%2Fcounter");
		local.PasskeySignInUrl.ShouldBe("/Account/LoginWithPasskey?returnUrl=%2Fcounter");
		local.Providers.ShouldBeEmpty();

		var foreign = Ok(await methods.GetSignInMethods(new() { ReturnUrl = "https://evil.example/" }, Cancellation));
		foreign.PasswordSignInUrl.ShouldBe("/Account/Login?returnUrl=%2F");
		foreign.PasskeySignInUrl.ShouldBe("/Account/LoginWithPasskey?returnUrl=%2F");

		var options = Ok(await methods.GetPasskeyRequestOptions(Cancellation));
		options.OptionsJson.ShouldContain("\"challenge\"");
		options.OptionsJson.ShouldContain("\"userVerification\":\"required\"");
		options.AssertionState.ShouldNotBeNullOrWhiteSpace();
	}

	// T3-28: the stranded-account verification runs in the web host, not the worker (Yggdrasil/CLAUDE.md: the worker is a
	// stub not grown ahead of the hosting abstractions). Every other test host disables it (TestHostEnvironment); this one
	// enables it on a one-second interval and watches the real service and the real query through their public gauge.
	// An account with neither a password nor a login is reported only on its second sighting, so the gauge's first
	// non-zero value is the one stranded account seeded here: every other account on this database has a password.
	[Fact]
	async Task The_web_host_runs_the_invariant_verification_service()
	{
		using var host = await WebServerHost.StartAsync(postgres, static builder => builder
			.UseSetting("Identity:Verification:Enabled", "true")
			.UseSetting("Identity:Verification:Interval", "00:00:01"));
		var meters = host.Services.GetRequiredService<IMeterFactory>();
		TaskCompletionSource<int> reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using MeterListener listener = new();
		listener.InstrumentPublished = (instrument, subscriber) =>
		{
			if (instrument.Meter.Scope == meters && instrument.Meter.Name == "Norse.Identity"
				&& instrument.Name == "norse.identity.stranded_accounts")
				subscriber.EnableMeasurementEvents(instrument);
		};
		listener.SetMeasurementEventCallback<int>((_, value, _, _) =>
		{
			if (value is not 0)
				reported.TrySetResult(value);
		});
		listener.Start();

		var stranded = $"stranded-{Guid.NewGuid():N}@example.com";
		using (var scope = host.Services.CreateScope())
		{
			var users = scope.ServiceProvider.GetRequiredService<UserManager<NorseUser>>();
			(await users.CreateAsync(new NorseUser { UserName = stranded, Email = stranded })).Succeeded.ShouldBeTrue();
		}

		(await reported.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancellation)).ShouldBe(1);
	}

	// Train 4 (2026-10-29 OpenIddict spec §7.5, §10.12, amendment r6.2): ReferencePolicies.Public is the one declared
	// exception to the generated identity-lane default -- it admits the identity and machine lanes, so the REST façade
	// (Machine + the Norse.Facade audience at the endpoint, Public at the endpoint and again on CountryQuery in the
	// mediator) serves a machine token, while the gRPC lane never routes to Norse.Machine. Every fact below runs on the
	// real Program.cs against real minted cookies and tokens.

	// Endpoint and mediator authorization both pass: a refusal at either would answer 403, never the payload.
	[Fact]
	async Task A_real_machine_token_reaches_the_rest_country_lookup()
	{
		using var host = await WebServerHost.StartAsync(postgres);
		var token = await MachineAccessTokenAsync(host);
		using var session = host.CreateWireSession();

		using var response = await FacadeLookupAsync(session, token);

		var body = await response.Content.ReadAsStringAsync(Cancellation);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
		var country = (await response.Content.ReadFromJsonAsync<FacadeCountry>(Cancellation)).ShouldNotBeNull();
		country.Alpha2.ShouldBe("US");
		country.Alpha3.ShouldBe("USA");
	}

	// The façade routes straight to Norse.Machine, so a signed-in cookie is never even authenticated there.
	[Fact]
	async Task An_identity_cookie_alone_is_refused_by_the_rest_facade()
	{
		using var host = await WebServerHost.StartAsync(postgres);
		using var session = await SignInAsync(host, await SeedAsync(host, "facade-cookie"));

		using var response = await FacadeLookupAsync(session, bearer: null);

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Fact]
	async Task A_cookie_grpc_lookup_still_succeeds()
	{
		using var host = await WebServerHost.StartAsync(postgres);
		using var session = await SignInAsync(host, await SeedAsync(host, "grpc-cookie"));

		Ok(await session.Service<IReferenceService>().GetCountry(new() { CodeInput = "US" }, Cancellation)).Alpha2
			.ShouldBe("US");
	}

	// A user token reaches the façade's Norse.Machine lane like any bearer, and fails authentication there (final lanes
	// ruling: the lane admits only its own token kind, Norse.Facade); a valid identity cookie beside it changes nothing,
	// because the façade never reads the cookie. The answer is the authentication challenge, bare. The same token still
	// reaches GetAccountStatus (AccountRead admits the user lane): the refusal is the token's kind, not a bad token.
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	async Task A_user_bearer_is_refused_by_the_rest_facade(bool withCookie)
	{
		using var host = await WebServerHost.StartAsync(postgres);
		var email = await SeedAsync(host, "user-bearer");
		using var signedIn = await SignInAsync(host, email);
		var token = await UserAccessTokenAsync(host, signedIn);
		using var bearer = host.CreateBearerSession(token);
		Ok(await StatusAsync(bearer)).HasPassword.ShouldBeTrue();

		using var response = await FacadeLookupAsync(withCookie ? signedIn : bearer, token);

		await ShouldBeBare401Async(response);
	}

	// A bearer header on the gRPC lane selects Norse.User, never Norse.Machine (NorseLaneSelector routes gRPC through
	// the identity-cookie handler), and a machine token fails authentication there: Machine in ReferencePolicies.Public
	// opens nothing to machine tokens over gRPC.
	[Fact]
	async Task A_machine_token_alone_is_refused_by_the_grpc_lookup()
	{
		using var host = await WebServerHost.StartAsync(postgres);
		var token = await MachineAccessTokenAsync(host);
		using var bearer = host.CreateBearerSession(token);

		Fails(await bearer.Service<IReferenceService>().GetCountry(new() { CodeInput = "US" }, Cancellation)).Category
			.ShouldBe(ErrorCategory.Unauthorized);
		await ShouldBeBare401Async(await RawGrpcAsync(host, GetCountryPath, token));
	}

	// Presented on the gRPC lane, the machine token rides Norse.User and fails authentication there (it carries
	// Norse.Facade, never Norse.User), before any policy runs: Self, AccountRead, Account and AuthNPolicies.Public all
	// answer the bare challenge.
	[Fact]
	async Task A_machine_token_is_refused_by_self_account_read_account_and_public()
	{
		using var host = await WebServerHost.StartAsync(postgres);
		var token = await MachineAccessTokenAsync(host);
		using var bearer = host.CreateBearerSession(token);

		Fails(await bearer.Service<IIdentityService>().GetMyPersonalDataAsync(new(), Cancellation)).Category
			.ShouldBe(ErrorCategory.Unauthorized);
		Fails(await StatusAsync(bearer)).Category.ShouldBe(ErrorCategory.Unauthorized);
		Fails(await bearer.Service<IAccountService>().GetExternalLogins(Cancellation)).Category
			.ShouldBe(ErrorCategory.Unauthorized);
		Fails(await bearer.Service<ISignInMethodsService>().GetSignInMethods(new() { ReturnUrl = "/" }, Cancellation)).Category
			.ShouldBe(ErrorCategory.Unauthorized);
		foreach (var path in new[] { GetMyPersonalDataPath, GetAccountStatusPath, GetExternalLoginsPath, GetSignInMethodsPath })
			await ShouldBeBare401Async(await RawGrpcAsync(host, path, token));
	}

	// Final lanes ruling, fact 3: the token's kind is decided at authentication, so a policy that names only the user lane,
	// with no audience pin behind it, still refuses a machine token. Self is replaced by exactly RequireLane(User) for this
	// host; the same policy admits a real user token, so the refusal is the machine token's kind, not the policy.
	[Fact]
	async Task A_user_lane_only_policy_refuses_a_real_machine_token()
	{
		using var host = await WebServerHost.StartAsync(postgres, static builder => builder
			.ConfigureTestServices(static services => services.PostConfigure<AuthorizationOptions>(static options =>
				options.AddPolicy(IdentityPolicies.Self, static policy => policy.RequireLane(NorseLanes.User)))));
		await ShouldBeLaneOnlyAsync(host, IdentityPolicies.Self, NorseLanes.User);
		var machine = await MachineAccessTokenAsync(host);
		using var machineBearer = host.CreateBearerSession(machine);

		Fails(await machineBearer.Service<IIdentityService>().GetMyPersonalDataAsync(new(), Cancellation)).Category
			.ShouldBe(ErrorCategory.Unauthorized);
		await ShouldBeBare401Async(await RawGrpcAsync(host, GetMyPersonalDataPath, machine));

		var email = await SeedAsync(host, "user-lane-only");
		using var signedIn = await SignInAsync(host, email);
		using var userBearer = host.CreateBearerSession(await UserAccessTokenAsync(host, signedIn));
		Ok(await userBearer.Service<IIdentityService>().GetMyPersonalDataAsync(new(), Cancellation)).Email.ShouldBe(email);
	}

	// The reverse: the façade's Machine policy is replaced by exactly RequireLane(Machine), dropping its Norse.Facade pin
	// (ReferencePolicies.Public, on the endpoint and the query, pins no audience either), and a real user token is still
	// refused, by authentication; a real machine token still reads.
	[Fact]
	async Task A_machine_lane_only_policy_refuses_a_real_user_token()
	{
		using var host = await WebServerHost.StartAsync(postgres, static builder => builder
			.ConfigureTestServices(static services => services.PostConfigure<AuthorizationOptions>(static options =>
				options.AddPolicy(NorsePolicies.Machine, static policy => policy.RequireLane(NorseLanes.Machine)))));
		await ShouldBeLaneOnlyAsync(host, NorsePolicies.Machine, NorseLanes.Machine);
		using var signedIn = await SignInAsync(host, await SeedAsync(host, "machine-lane-only"));
		var user = await UserAccessTokenAsync(host, signedIn);
		using var session = host.CreateWireSession();

		using (var refused = await FacadeLookupAsync(session, user))
			await ShouldBeBare401Async(refused);

		using var admitted = await FacadeLookupAsync(session, await MachineAccessTokenAsync(host));
		admitted.StatusCode.ShouldBe(HttpStatusCode.OK, await admitted.Content.ReadAsStringAsync(Cancellation));
	}

	// Final lanes ruling, fact 4: a bearer header selects the bearer path, and a wrong-kind token fails there with no
	// fallback to the valid identity cookie riding the same call; the same cookie alone still passes.
	[Fact]
	async Task A_wrong_kind_bearer_with_a_valid_identity_cookie_fails_on_the_bearer_path()
	{
		using var host = await WebServerHost.StartAsync(postgres);
		using var signedIn = await SignInAsync(host, await SeedAsync(host, "wrong-kind-cookie"));
		var cookie = signedIn.Cookie(IdentityCookie).ShouldNotBeNull();
		var machine = await MachineAccessTokenAsync(host);
		using var both = host.CreateBearerSession(machine, (IdentityCookie, cookie));

		Fails(await StatusAsync(both)).Category.ShouldBe(ErrorCategory.Unauthorized);
		Fails(await both.Service<IReferenceService>().GetCountry(new() { CodeInput = "US" }, Cancellation)).Category
			.ShouldBe(ErrorCategory.Unauthorized);
		await ShouldBeBare401Async(await RawGrpcAsync(host, GetAccountStatusPath, machine, cookie));

		using var cookieOnly = host.CreateWireSession((IdentityCookie, cookie));
		Ok(await StatusAsync(cookieOnly)).HasPassword.ShouldBeTrue();
	}

	// Final lanes ruling, fact 5: the user bearer's two operations stay open to it (AccountRead and Self admit the user
	// lane, pinned to Norse.User), and the machine façade read stays green (A_real_machine_token_reaches_the_rest_country_lookup).
	[Fact]
	async Task A_user_bearer_still_reads_its_own_personal_data_and_account_status()
	{
		using var host = await WebServerHost.StartAsync(postgres);
		var email = await SeedAsync(host, "user-bearer-reads");
		using var signedIn = await SignInAsync(host, email);
		using var bearer = host.CreateBearerSession(await UserAccessTokenAsync(host, signedIn));

		Ok(await bearer.Service<IIdentityService>().GetMyPersonalDataAsync(new(), Cancellation)).Email.ShouldBe(email);
		Ok(await StatusAsync(bearer)).HasPassword.ShouldBeTrue();
	}

	const string GetCountryPath = "/grpc.reference.v1.ReferenceService/GetCountry";
	const string GetMyPersonalDataPath = "/grpc.identity.v1.IdentityService/GetMyPersonalData";
	const string GetAccountStatusPath = "/grpc.account.v1.AccountService/GetAccountStatus";
	const string GetExternalLoginsPath = "/grpc.account.v1.AccountService/GetExternalLogins";
	const string GetSignInMethodsPath = "/grpc.signinmethods.v1.SignInMethodsService/GetSignInMethods";

	/// <summary>
	///     A raw unary gRPC call to <paramref name="path" /> over HTTP/2 with <c>Authorization: Bearer</c>
	///     <paramref name="bearer" /> and, if any, the identity cookie: the HTTP answer the typed client decodes away.
	/// </summary>
	static async Task<HttpResponseMessage> RawGrpcAsync(WebServerHost host, string path, string bearer, string? cookie = null)
	{
		using var client = host.CreateBrowserClient();
		using HttpRequestMessage request = new(HttpMethod.Post, new Uri(path, UriKind.Relative))
		{
			Content = WebServerHost.EmptyGrpcBody()
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
		if (cookie is not null)
			request.Headers.Add("Cookie", $"{IdentityCookie}={cookie}");
		return await client.SendAsync(request, Cancellation);
	}

	/// <summary>
	///     The composed authentication-challenge shape (final lanes ruling): HTTP 401, no redirect, no body, and one
	///     <c>WWW-Authenticate: Bearer</c> carrying at most <c>error="invalid_token"</c>, never a description or a URI.
	/// </summary>
	static async Task ShouldBeBare401Async(HttpResponseMessage response)
	{
		using (response)
		{
			response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
			response.Headers.Location.ShouldBeNull();
			(await response.Content.ReadAsStringAsync(Cancellation)).ShouldBeEmpty();
			var challenge = response.Headers.WwwAuthenticate.ShouldHaveSingleItem();
			challenge.Scheme.ShouldBe("Bearer");
			challenge.Parameter.ShouldBeOneOf([null, "error=\"invalid_token\""], "at most the bare error, never a description or a URI");
		}
	}

	/// <summary>The host's <paramref name="name" /> policy is exactly one lane requirement, <paramref name="lane" />.</summary>
	static async Task ShouldBeLaneOnlyAsync(WebServerHost host, string name, string lane)
	{
		var policy = (await host.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(name)).ShouldNotBeNull();
		policy.Requirements.ShouldHaveSingleItem().ShouldBeOfType<LaneRequirement>().Lanes.ShouldBe([lane]);
	}

	/// <summary>The REST façade's country lookup for <c>US</c>, as JSON, with <paramref name="bearer" /> if any.</summary>
	static async Task<HttpResponseMessage> FacadeLookupAsync(WireSession session, string? bearer)
	{
		using HttpRequestMessage request = new(HttpMethod.Get, "/api/reference/countries/US");
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		if (bearer is not null)
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
		return await session.SendAsync(request);
	}

	/// <summary>The façade's JSON country document, camelCase on the wire; only the members these facts read.</summary>
	sealed record FacadeCountry(string Alpha2, string Alpha3);

	/// <summary>The registered public client's redirect, a loopback URI as a native client's is.</summary>
	const string NativeRedirect = "http://127.0.0.1/cb";

	/// <summary>
	///     A user access token for <paramref name="session" />'s signed-in user: a public client is registered, the
	///     authorize endpoint issues a code to the session's fresh identity cookie, and the token endpoint redeems it with
	///     the PKCE verifier.
	/// </summary>
	static async Task<string> UserAccessTokenAsync(WebServerHost host, WireSession session)
	{
		var clientId = $"native-{Guid.NewGuid():N}";
		using (var scope = host.Services.CreateScope())
		{
			OpenIddictApplicationDescriptor descriptor = new()
			{
				ClientId = clientId,
				ClientType = OpenIddictConstants.ClientTypes.Public,
				ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
				Permissions =
				{
					OpenIddictConstants.Permissions.Endpoints.Authorization,
					OpenIddictConstants.Permissions.Endpoints.Token,
					OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
					OpenIddictConstants.Permissions.ResponseTypes.Code
				},
				Requirements = { OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange }
			};
			descriptor.RedirectUris.Add(new Uri(NativeRedirect));
			await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(descriptor, Cancellation);
		}

		var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
		var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
		var query = QueryString.Create([
			new KeyValuePair<string, string?>(OpenIddictConstants.Parameters.ClientId, clientId),
			new(OpenIddictConstants.Parameters.ResponseType, OpenIddictConstants.ResponseTypes.Code),
			new(OpenIddictConstants.Parameters.RedirectUri, NativeRedirect),
			new(OpenIddictConstants.Parameters.Scope, OpenIddictConstants.Scopes.OpenId),
			new(OpenIddictConstants.Parameters.State, "state"),
			new(OpenIddictConstants.Parameters.CodeChallenge, challenge),
			new(OpenIddictConstants.Parameters.CodeChallengeMethod, OpenIddictConstants.CodeChallengeMethods.Sha256)
		]);
		using var authorize = await session.GetAsync("/connect/authorize" + query);
		var location = authorize.Headers.Location?.OriginalString;
		authorize.StatusCode.ShouldBe(HttpStatusCode.Redirect, location);
		location.ShouldNotBeNull().ShouldStartWith($"{NativeRedirect}?");
		var code = QueryHelpers.ParseQuery(new Uri(location).Query)[OpenIddictConstants.Parameters.Code].ToString();

		using var redeemed = await session.PostFormAsync("/connect/token", [
			new(OpenIddictConstants.Parameters.GrantType, OpenIddictConstants.GrantTypes.AuthorizationCode),
			new(OpenIddictConstants.Parameters.Code, code),
			new(OpenIddictConstants.Parameters.RedirectUri, NativeRedirect),
			new(OpenIddictConstants.Parameters.ClientId, clientId),
			new(OpenIddictConstants.Parameters.CodeVerifier, verifier)
		]);
		return await AccessTokenAsync(redeemed);
	}

	/// <summary>A machine access token: a confidential client is registered and granted client credentials.</summary>
	static async Task<string> MachineAccessTokenAsync(WebServerHost host)
	{
		var clientId = $"machine-{Guid.NewGuid():N}";
		const string Secret = "machine-secret";
		using (var scope = host.Services.CreateScope())
			await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(new OpenIddictApplicationDescriptor
			{
				ClientId = clientId,
				ClientSecret = Secret,
				ClientType = OpenIddictConstants.ClientTypes.Confidential,
				Permissions =
				{
					OpenIddictConstants.Permissions.Endpoints.Token,
					OpenIddictConstants.Permissions.GrantTypes.ClientCredentials
				}
			}, Cancellation);

		using var session = host.CreateWireSession();
		using var response = await session.PostFormAsync("/connect/token", [
			new(OpenIddictConstants.Parameters.GrantType, OpenIddictConstants.GrantTypes.ClientCredentials),
			new(OpenIddictConstants.Parameters.ClientId, clientId),
			new(OpenIddictConstants.Parameters.ClientSecret, Secret)
		]);
		return await AccessTokenAsync(response);
	}

	static async Task<string> AccessTokenAsync(HttpResponseMessage response)
	{
		var body = await response.Content.ReadAsStringAsync(Cancellation);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
		var token = (await response.Content.ReadFromJsonAsync<TokenResponse>(Cancellation)).ShouldNotBeNull();
		return token.AccessToken;
	}

	/// <summary>The token response's access token, snake_case on the wire.</summary>
	sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);

	/// <summary>The stored stamp and the session's identity cookie, read just before a step.</summary>
	readonly record struct Snapshot(string Stamp, string Cookie);

	static async Task<Snapshot> SnapshotAsync(WebServerHost host, WireSession session, string email) =>
		new(await StampAsync(host, email), session.Cookie(IdentityCookie).ShouldNotBeNull());

	/// <summary>
	///     A rotating step: the stored stamp moved, the cookie from before the step is refused (gRPC
	///     <c>UNAUTHENTICATED</c>, which the client decodes to <see cref="ErrorCategory.Unauthorized" />), and the
	///     session's refreshed cookie passes.
	/// </summary>
	static async Task ShouldRotateAsync(WebServerHost host, WireSession session, string email, Snapshot before)
	{
		(await StampAsync(host, email)).ShouldNotBe(before.Stamp);
		using var stale = host.CreateWireSession((IdentityCookie, before.Cookie));
		Fails(await StatusAsync(stale)).Category.ShouldBe(ErrorCategory.Unauthorized);
		session.Cookie(IdentityCookie).ShouldNotBe(before.Cookie);
		Ok(await StatusAsync(session));
	}

	/// <summary>A non-rotating step: the stored stamp is unchanged, and the cookie from before the step still passes.</summary>
	static async Task ShouldNotRotateAsync(WebServerHost host, string email, Snapshot before)
	{
		(await StampAsync(host, email)).ShouldBe(before.Stamp);
		using var earlier = host.CreateWireSession((IdentityCookie, before.Cookie));
		Ok(await StatusAsync(earlier));
	}

	/// <summary>Seeds a confirmed-password account through the host's own <c>NorseUserManager</c>.</summary>
	static async Task<string> SeedAsync(WebServerHost host, string label)
	{
		var email = $"{label}-{Guid.NewGuid():N}@example.com";
		using var scope = host.Services.CreateScope();
		var users = scope.ServiceProvider.GetRequiredService<UserManager<NorseUser>>();
		var created = await users.CreateAsync(new NorseUser { UserName = email, Email = email }, Password);
		created.Succeeded.ShouldBeTrue(string.Join(", ", created.Errors.Select(static error => error.Description)));
		return email;
	}

	static async Task<string> StampAsync(WebServerHost host, string email)
	{
		using var scope = host.Services.CreateScope();
		var users = scope.ServiceProvider.GetRequiredService<UserManager<NorseUser>>();
		var user = (await users.FindByEmailAsync(email)).ShouldNotBeNull();
		return (await users.GetSecurityStampAsync(user)).ShouldNotBeNull();
	}

	/// <summary>
	///     A session holding only the anonymous cookie: the gRPC lane never mints one, so the first page GET does, as a
	///     browser's first page load would.
	/// </summary>
	static async Task<WireSession> AnonymousAsync(WebServerHost host, WireLane lane = WireLane.Grpc)
	{
		var session = host.CreateWireSession(lane);
		using var page = await session.GetAsync("/");
		page.EnsureSuccessStatusCode();
		return session;
	}

	static async Task<WireSession> SignInAsync(WebServerHost host, string email, WireLane lane = WireLane.Grpc)
	{
		var session = await AnonymousAsync(host, lane);
		Ok(await LoginAsync(session, email, Password)).NextUrl.ShouldBe("/");
		session.Cookie(IdentityCookie).ShouldNotBeNull();
		return session;
	}

	static Task<Outcome<NavigationResult>> LoginAsync(WireSession session, string email, string password) =>
		session.Service<IAuthenticationService>().Login(new() { EmailInput = email, Password = password }, Cancellation);

	/// <summary>Setup then enable, as the enable page does; answers the enrolled key.</summary>
	static async Task<string> EnableTwoFactorAsync(WireSession session)
	{
		var twoFactor = session.Service<ITwoFactorService>();
		var key = Ok(await twoFactor.GetAuthenticatorSetup(Cancellation)).SharedKey;
		Ok(await twoFactor.EnableAuthenticator(new() { Code = TotpCode.Now(Raw(key)) }, Cancellation));
		return key;
	}

	/// <summary>A password sign-in answered by the second factor, remembering this browser.</summary>
	static async Task SignInRememberingAsync(WireSession session, string email, string key)
	{
		Uri challengeUrl = new(WireSession.Origin, Ok(await LoginAsync(session, email, Password)).NextUrl);
		challengeUrl.AbsolutePath.ShouldBe("/Account/LoginWith2fa");
		var challenge = QueryHelpers.ParseQuery(challengeUrl.Query)["challenge"].ToString();

		Ok(await session.Service<IAuthenticationService>().TwoFactorSignIn(
			new() { Challenge = challenge, Code = TotpCode.Now(Raw(key)), RememberMachine = true }, Cancellation)).NextUrl.ShouldBe("/");
	}

	static Task<Outcome<AccountStatusResponse>> StatusAsync(WireSession session) =>
		session.Service<IAccountService>().GetAccountStatus(Cancellation);

	/// <summary>The shown key (lowercase groups of four) as the base32 the app scans.</summary>
	static string Raw(string sharedKey) =>
		sharedKey.Replace(" ", string.Empty, StringComparison.Ordinal);

	static T Ok<T>(Outcome<T> outcome) where T : notnull
	{
		if (outcome.TryGetValue(out Failed failed))
			throw new ShouldAssertException($"Expected success, got {failed.Problem.Category}: "
				+ string.Join("; ", failed.Problem.Errors?.SelectMany(static pair => pair.Value) ?? []));
		outcome.TryGetValue(out Success<T> success);
		return success.Value;
	}

	static Problem Fails<T>(Outcome<T> outcome) where T : notnull
	{
		outcome.TryGetValue(out Failed failed).ShouldBeTrue("Expected a failure, got success.");
		return failed.Problem;
	}

	static string Message(Problem problem) =>
		problem.Errors.ShouldNotBeNull().SelectMany(static pair => pair.Value).ShouldHaveSingleItem();
}
