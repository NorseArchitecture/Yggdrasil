using System.Net;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Norse.Infrastructure.Web.Client.Grpc;
using ProtoBuf.Grpc.Client;

namespace Norse.Hosting.Web.Server.Tests.Authentication;

/// <summary>The wire a <see cref="WireSession" />'s calls ride.</summary>
enum WireLane
{
	/// <summary>A <see cref="GrpcChannel" /> speaking gRPC over HTTP/2, as the server-side callers do.</summary>
	Grpc,

	/// <summary>
	///     Midgard's <see cref="GrpcWebCallInvoker" /> over a plain <see cref="HttpClient" />, speaking gRPC-Web, exactly as
	///     the WASM client composes it (<c>Hosting.Web.Client/Program.cs</c>); the jar stands in for the browser's
	///     credentialed fetch.
	/// </summary>
	GrpcWeb,
}

/// <summary>
///     One browser tab against a <see cref="WebServerHost" />: a cookie jar shared by plain page GETs and a gRPC lane
///     (<see cref="WireLane" />) decoded by the real <see cref="OutcomeClientInterceptor" />, the WASM client's own decoder. A gRPC call that signs
///     in or refreshes the sign-in sets its cookie on the call's own response (only a circuit defers it), so the jar
///     always holds what a browser would.
/// </summary>
sealed class WireSession : IDisposable
{
	/// <summary>
	///     The origin every request names. <c>https</c> is load-bearing: the identity and anonymous cookies are
	///     <c>Secure</c>, and a cookie container withholds a <c>Secure</c> cookie from a plain-<c>http</c> request.
	/// </summary>
	internal static readonly Uri Origin = new("https://localhost/");

	readonly IDisposable _transport;
	readonly CookieContainer _jar;
	readonly HttpClient _pages;
	readonly CallInvoker _invoker;

	internal WireSession(WebApplicationFactory<Program> factory, CookieContainer jar, WireLane lane, string? bearer = null)
	{
		_jar = jar;
		_pages = factory.CreateDefaultClient(Origin, new CookieContainerHandler(jar));
		if (lane is WireLane.GrpcWeb)
		{
#pragma warning disable CA2000 // The client owns its handler (disposeHandler) and the session owns the client.
			HttpClient client = new(new CookieContainerHandler(jar) { InnerHandler = factory.Server.CreateHandler() }, true)
			{
				BaseAddress = Origin
			};
#pragma warning restore CA2000
			_transport = client;
			_invoker = Authorized(new GrpcWebCallInvoker(client), bearer).Intercept(new OutcomeClientInterceptor());
		}
		else
		{
			var channel = GrpcChannel.ForAddress(Origin, new GrpcChannelOptions
			{
				HttpHandler = new CookieContainerHandler(jar) { InnerHandler = factory.Server.CreateHandler() }
			});
			_transport = channel;
			_invoker = Authorized(channel.CreateCallInvoker(), bearer).Intercept(new OutcomeClientInterceptor());
		}
	}

	/// <summary>
	///     <paramref name="invoker" /> sending <c>Authorization: Bearer</c> <paramref name="bearer" /> on every call, as a
	///     native or machine client does, or unchanged when there is none.
	/// </summary>
	static CallInvoker Authorized(CallInvoker invoker, string? bearer) =>
		bearer is null ?
			invoker :
			invoker.Intercept(metadata =>
			{
				metadata.Add("Authorization", $"Bearer {bearer}");
				return metadata;
			});

	/// <summary>A code-first gRPC client for <typeparamref name="TService" /> over this session's jar.</summary>
	internal TService Service<TService>() where TService : class =>
		_invoker.CreateGrpcService<TService>();

	/// <summary>A page GET carrying this session's cookies; redirects are not followed.</summary>
	/// <param name="path">The root-relative path.</param>
	internal async Task<HttpResponseMessage> GetAsync(string path) =>
		await _pages.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

	/// <summary>Sends <paramref name="request" /> carrying this session's cookies; redirects are not followed.</summary>
	/// <param name="request">The request, root-relative.</param>
	internal Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) =>
		_pages.SendAsync(request, TestContext.Current.CancellationToken);

	/// <summary>A form POST carrying this session's cookies; redirects are not followed.</summary>
	/// <param name="path">The root-relative path.</param>
	/// <param name="form">The form's fields.</param>
	internal async Task<HttpResponseMessage> PostFormAsync(string path, IEnumerable<KeyValuePair<string, string>> form)
	{
		using FormUrlEncodedContent content = new(form);
		return await _pages.PostAsync(new Uri(path, UriKind.Relative), content, TestContext.Current.CancellationToken);
	}

	/// <summary>The value of the cookie named <paramref name="name" />, or <see langword="null" /> when the jar has none.</summary>
	/// <param name="name">The cookie name.</param>
	internal string? Cookie(string name) =>
		_jar.GetCookies(Origin)[name]?.Value;

	/// <inheritdoc />
	public void Dispose()
	{
		_transport.Dispose();
		_pages.Dispose();
	}
}
