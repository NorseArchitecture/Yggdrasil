using Microsoft.AspNetCore.Mvc.Testing;

namespace Norse.Hosting.Web.Server.Tests.Authentication;

/// <summary>
///     <see cref="LaneCompositionTests" />'s test host — a real <c>Program.cs</c> boot via
///     <see cref="WebApplicationFactory{TEntryPoint}" />, mirroring <see cref="CompositionTests" />'s
///     env-var mechanics: this assembly's <see cref="TestHostEnvironment" /> module initializer already
///     stamps the fake <c>ConnectionStrings__norse_identity</c>/<c>norse_reference</c> values
///     <c>Program.cs</c> reads before <c>builder.Build()</c>, so nothing here opens a real connection.
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
	internal static Task<WebServerHost> StartAsync()
	{
		WebApplicationFactory<Program> factory = new();
		var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		return Task.FromResult(new WebServerHost(factory, client));
	}

	public void Dispose()
	{
		Client.Dispose();
		_factory.Dispose();
	}
}
