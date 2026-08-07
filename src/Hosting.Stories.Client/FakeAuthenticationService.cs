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
	// LoginResult.Succeeded was deleted platform-wide (ruled 2026-08-06, see the type's own doc
	// comment) -- a rejected login is a Failed(Problem) instead, never a bare-success record with a
	// false flag. This fake always reports success, with the next-hop URL always resolved to "/" --
	// there is no real sign-in flow behind this story-host fake, so no 2FA challenge or deferred
	// completion URL is ever produced.
	public Task<Outcome<LoginResult>> Login(LoginRequest request, CancellationToken cancellationToken = default) =>
		Task.FromResult(Outcome<LoginResult>.Ok(new LoginResult { NextUrl = "/" }));

	public Task<Outcome<RegisterResult>> Register(RegisterRequest request, CancellationToken cancellationToken = default) =>
		Task.FromResult(Outcome<RegisterResult>.Ok(new RegisterResult { Succeeded = true }));

	// Always reports "not taken" -- a story-host fake with no real user store behind it; there is
	// nothing for a checked email to ever collide with.
	public Task<Outcome<BoolResponse>> EmailExists(EmailExistsRequest request, CancellationToken cancellationToken = default) =>
		Task.FromResult(Outcome<BoolResponse>.Ok(new BoolResponse { Value = false }));

	public Task<Outcome<LogoutResult>> Logout(CancellationToken cancellationToken = default) =>
		Task.FromResult(Outcome<LogoutResult>.Ok(new LogoutResult()));
}
