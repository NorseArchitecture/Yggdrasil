using Norse.Abstractions.Contracts;
using Norse.AuthN.Services;

namespace Norse.Hosting.Stories.Client;

/// <summary>
/// Story-host-only stand-in for <see cref="IAuthenticationService"/> — never calls Himinbjörg, never
/// touches gRPC. Exists so Bragi's Login/Register/Logout stories render and are interactive with no
/// server context, per Bragi's charter (content/markup only, no real backend calls from the catalog).
/// </summary>
sealed class FakeAuthenticationService : IAuthenticationService
{
	public Task<Outcome<LoginResult>> Login(LoginRequest request, CancellationToken cancellationToken = default) =>
		Task.FromResult(Outcome<LoginResult>.Ok(new LoginResult { Succeeded = true }));

	public Task<Outcome<RegisterResult>> Register(RegisterRequest request, CancellationToken cancellationToken = default) =>
		Task.FromResult(Outcome<RegisterResult>.Ok(new RegisterResult { Succeeded = true }));

	public Task<Outcome<LogoutResult>> Logout(CancellationToken cancellationToken = default) =>
		Task.FromResult(Outcome<LogoutResult>.Ok(new LogoutResult()));
}
