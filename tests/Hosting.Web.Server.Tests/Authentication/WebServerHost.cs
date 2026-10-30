using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Norse.Infrastructure.Backend.Keys;
using Norse.Infrastructure.Web.Server.Authentication;

namespace Norse.Hosting.Web.Server.Tests.Authentication;

/// <summary>
///     <see cref="LaneCompositionTests" />'s test host — a real <c>Program.cs</c> boot via
///     <see cref="WebApplicationFactory{TEntryPoint}" />, mirroring <see cref="CompositionTests" />'s
///     env-var mechanics: this assembly's <see cref="TestHostEnvironment" /> module initializer already
///     stamps the fake <c>ConnectionStrings__norse_identity</c>/<c>norse_reference</c> values
///     <c>Program.cs</c> reads before <c>builder.Build()</c>, so nothing here opens a real connection -- except the
///     lane host, <see cref="StartAsync(MachineAuthPostgresFixture, Action{IWebHostBuilder})" />, which points the same boot at a migrated
///     database for <see cref="CompositionLaneTests" />.
///     One instance per fact rather than a shared <see cref="IClassFixture{TFixture}" /> — cheap enough
///     for the facts in this file, and each fact wants its own unshared cookie jar anyway. Backs facts 1-3
///     only: fact 4 needs a real listening socket for Playwright to connect to (an in-memory
///     <see cref="Microsoft.AspNetCore.TestHost.TestServer" /> has none) and uses
///     <see cref="CircuitCompositionFixture" /> instead.
/// </summary>
sealed class WebServerHost : IDisposable
{
	readonly WebApplicationFactory<Program> _factory;

	WebServerHost(WebApplicationFactory<Program> factory, HttpClient client)
	{
		_factory = factory;
		Client = client;
	}

	/// <summary>The booted host's root service provider (composition-assertion facts read policies off this).</summary>
	internal IServiceProvider Services => _factory.Services;

	/// <summary>
	///     An <see cref="HttpClient" /> against the booted host with automatic redirect-following
	///     disabled — a lane-composition assertion cares about the response the server actually sent
	///     (status code, headers), not where a client-side redirect chain eventually lands.
	/// </summary>
	internal HttpClient Client { get; }

	/// <summary>Boots the real composition root and returns a disposable handle to it.</summary>
	internal static Task<WebServerHost> StartAsync() =>
		Task.FromResult(Start(new WebApplicationFactory<Program>()));

	/// <summary>
	///     Boots the real composition root against <paramref name="postgres" />'s migrated identity and reference
	///     databases: the lane host (T14). The connection strings ride host settings, which the factory hands
	///     <c>Program.cs</c> as command-line arguments, so its pre-<c>Build()</c> reads see them. The key seam is the
	///     fixture's own root, and <see cref="SecurityStampValidatorOptions.ValidationInterval" /> is zero, so the stock
	///     <see cref="SecurityStampValidator" /> checks the stamp on every request through
	///     <c>NorseSignInManager.ValidateSecurityStampAsync</c>: a cookie minted before a stamp rotation is refused on
	///     its very next request.
	/// </summary>
	/// <param name="postgres">The fixture holding the databases and the key root.</param>
	/// <param name="configure">Further host settings for one fact, applied after the lane's own.</param>
	internal static Task<WebServerHost> StartAsync(MachineAuthPostgresFixture postgres,
		Action<IWebHostBuilder>? configure = null) =>
		Task.FromResult(Start(new PostgresWebServerFactory(postgres, configure)));

	static WebServerHost Start(WebApplicationFactory<Program> factory) =>
		new(factory, factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));

	/// <summary>
	///     A cookie-carrying session against the booted host: page GETs and gRPC calls share one jar, as a browser tab's
	///     page and its gRPC-Web channel do. <paramref name="cookies" /> seeds the jar, for a session that presents
	///     exactly one remembered cookie and nothing else.
	/// </summary>
	/// <param name="cookies">The cookies the session starts with, by name.</param>
	internal WireSession CreateWireSession(params (string Name, string Value)[] cookies) =>
		CreateWireSession(WireLane.Grpc, cookies);

	/// <summary>A cookie-carrying session over <paramref name="lane" />; otherwise as the overload without one.</summary>
	/// <param name="lane">The wire the session's calls ride.</param>
	/// <param name="cookies">The cookies the session starts with, by name.</param>
	internal WireSession CreateWireSession(WireLane lane, params (string Name, string Value)[] cookies)
	{
		CookieContainer jar = new();
		foreach (var (name, value) in cookies)
			jar.Add(WireSession.Origin, new Cookie(name, value) { Secure = true });
		return new(_factory, jar, lane);
	}

	/// <summary>
	///     A session with no cookies whose every gRPC call carries <c>Authorization: Bearer</c>
	///     <paramref name="accessToken" />, as a native or machine client calls; page requests carry nothing.
	/// </summary>
	/// <param name="accessToken">The access token to present.</param>
	internal WireSession CreateBearerSession(string accessToken) =>
		CreateBearerSession(accessToken, []);

	/// <summary>
	///     A bearer session as the overload without cookies, whose jar also starts with <paramref name="cookies" />: a
	///     caller presenting a bearer and a cookie at once.
	/// </summary>
	/// <param name="accessToken">The access token to present.</param>
	/// <param name="cookies">The cookies the session starts with, by name.</param>
	internal WireSession CreateBearerSession(string accessToken, params (string Name, string Value)[] cookies)
	{
		CookieContainer jar = new();
		foreach (var (name, value) in cookies)
			jar.Add(WireSession.Origin, new Cookie(name, value) { Secure = true });
		return new(_factory, jar, WireLane.Grpc, accessToken);
	}

	/// <summary>
	///     A fresh <see cref="HttpClient" /> against the same booted host, configured like a real browser
	///     tab: no auto-follow on redirects (a lane-composition assertion cares about the server's own
	///     response, not where a client-side redirect chain lands) and cookies persisted across requests on
	///     this client instance (<see cref="WebApplicationFactoryClientOptions.HandleCookies" />'s default),
	///     so the anonymous cookie the browser lane's anonymous handler mints on one request rides along on
	///     the next, the way a tab's own cookie jar would. The <c>https://</c> base address is load-bearing,
	///     not cosmetic: <see cref="NorseAnonymousOptions.BuildCookieOptions" /> marks the cookie
	///     <c>Secure</c>, and <see cref="HttpClient" />'s own cookie container silently withholds a
	///     <c>Secure</c> cookie from a plain-<c>http</c> request — the default base address
	///     <see cref="WebApplicationFactory{TEntryPoint}" /> would otherwise use. HTTP/2 is requested
	///     explicitly for the same reason <see cref="EmptyGrpcBody" />'s callers already need it
	///     (mirrors Midgard's <c>LaneHost</c>): a gRPC endpoint answers 426 Upgrade Required to a plain
	///     HTTP/1.1 request once authentication lets it reach routing, and the plain 401/403 lane tests
	///     never got far enough into the pipeline to notice the client was never asking for HTTP/2.
	/// </summary>
	internal HttpClient CreateBrowserClient()
	{
		var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
			BaseAddress = new Uri("https://localhost"),
		});
		client.DefaultRequestVersion = HttpVersion.Version20;
		client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
		return client;
	}

	/// <summary>
	///     A <see cref="CreateBrowserClient" />-shaped client whose default <c>Cookie</c> header carries a
	///     freshly minted <see cref="IdentityConstants.ApplicationScheme" /> ticket: a signed-in identity
	///     principal without a database. The ticket is protected by the booted host's own
	///     <see cref="CookieAuthenticationOptions.TicketDataFormat" /> and carries exactly the three
	///     allowlisted claims (user id, role, security stamp) under the host's
	///     <see cref="IdentityOptions.ClaimsIdentity" /> claim types. Issued now, it sits inside
	///     <see cref="SecurityStampValidatorOptions.ValidationInterval" />, so the cookie handler never
	///     reads the store on the way through.
	/// </summary>
	internal HttpClient CreateIdentityClient()
	{
		var cookieOptions = Services
			.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
			.Get(IdentityConstants.ApplicationScheme);
		var claimTypes = Services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity;
		ClaimsPrincipal principal = new(new ClaimsIdentity(
		[
			new Claim(claimTypes.UserIdClaimType, Guid.CreateVersion7().ToString()),
			new Claim(claimTypes.RoleClaimType, "user"),
			new Claim(claimTypes.SecurityStampClaimType, "stamp"),
		], IdentityConstants.ApplicationScheme, claimTypes.UserNameClaimType, claimTypes.RoleClaimType));
		var now = DateTimeOffset.UtcNow;
		AuthenticationTicket ticket = new(principal,
			new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now.AddHours(1) },
			IdentityConstants.ApplicationScheme);

		var client = CreateBrowserClient();
		client.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={cookieOptions.TicketDataFormat.Protect(ticket)}");
		return client;
	}

	/// <summary>
	///     A well-formed, zero-length unary gRPC request body: a 5-byte frame (no compression, zero-length
	///     message) -- enough for routing/authentication to run without a real serialized message. Mirrors
	///     Midgard's <c>Infrastructure.Web.Server.Tests/Authentication/LaneHost.EmptyGrpcBody</c>.
	/// </summary>
	internal static HttpContent EmptyGrpcBody()
	{
		ByteArrayContent content = new([0, 0, 0, 0, 0]);
		content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
		return content;
	}

	public void Dispose()
	{
		Client.Dispose();
		_factory.Dispose();
	}

	sealed class PostgresWebServerFactory(MachineAuthPostgresFixture postgres, Action<IWebHostBuilder>? configure)
		: WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder
				.UseSetting("ConnectionStrings:norse_identity", postgres.IdentityConnectionString)
				.UseSetting("ConnectionStrings:norse_reference", postgres.ReferenceConnectionString)
				.ConfigureTestServices(services => services
					.AddNorseDevelopmentKeys(postgres.KeysRoot)
					.Configure<SecurityStampValidatorOptions>(static options => options.ValidationInterval = TimeSpan.Zero));
			configure?.Invoke(builder);
		}
	}
}
