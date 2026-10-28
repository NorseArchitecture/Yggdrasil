using System.Buffers.Text;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Norse.Abstractions.Contracts;
using Norse.AuthN.Services;
using Norse.Hosting.Web.Server.Tests.Authentication;
using Norse.Identity.EntityFramework;
using Norse.Primitives;

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
