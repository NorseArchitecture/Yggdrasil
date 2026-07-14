using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Norse.Hosting.Web.Client;

/// <summary>
/// Sets <c>credentials: 'include'</c> on every outgoing gRPC-Web request so the browser sends and
/// stores the cookie <c>IAuthenticationService.Login</c>/<c>.Logout</c> mint/clear server-side.
/// </summary>
sealed class BrowserCredentialsHandler : DelegatingHandler
{
	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
		return base.SendAsync(request, cancellationToken);
	}
}
