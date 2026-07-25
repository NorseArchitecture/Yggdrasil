using Norse.Abstractions.Contracts;
using Norse.AuthN.Services;

namespace Norse.Hosting.Stories.Client;

/// <summary>
/// Story-host-only stand-in for <see cref="IAuthenticationGateway"/> — never calls Himinbjörg, never
/// touches gRPC. Exists so Bragi's Login/Register/Logout stories render and are interactive with no
/// server context, per Bragi's charter (content/markup only, no real backend calls from the catalog).
/// </summary>
sealed class FakeAuthenticationGateway : IAuthenticationGateway
{
	public ValueTask<Outcome<LoginResult>> Login(LoginRequest request, CancellationToken cancellationToken = default) =>
		new(Outcome<LoginResult>.Ok(new LoginResult { Succeeded = true }));

	public ValueTask<Outcome<Unit>> Register(RegisterRequest request, CancellationToken cancellationToken = default) =>
		new(Outcome<Unit>.Ok(Unit.Value));

	public ValueTask<Outcome<LogoutResult>> Logout(LogoutRequest request, CancellationToken cancellationToken = default) =>
		new(Outcome<LogoutResult>.Ok(new LogoutResult()));
}
