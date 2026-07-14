using Norse.AuthN.Components;

namespace Norse.Hosting.Stories.Client;

/// <summary>
/// Story-host-only stand-in for <see cref="IAuthenticationGateway"/> — never calls Himinbjörg, never
/// touches gRPC. Exists so Bragi's Login/Register/Logout stories render and are interactive with no
/// server context, per Bragi's charter (content/markup only, no real backend calls from the catalog).
/// </summary>
sealed class FakeAuthenticationGateway : IAuthenticationGateway
{
	static readonly AuthenticationResult _success = new() { Succeeded = true };

	public Task<AuthenticationResult> Login(LoginRequest request) => Task.FromResult(_success);

	public Task<AuthenticationResult> Register(RegisterRequest request) => Task.FromResult(_success);

	public Task<AuthenticationResult> Logout(LogoutRequest request) => Task.FromResult(_success);
}
