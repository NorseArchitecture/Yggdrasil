using Grpc.Core;
using Norse.AuthN.Components;
using Norse.Infrastructure.Web.Client.Grpc;

namespace Norse.Hosting.Web.Client;

/// <summary>
/// WASM's <see cref="IAuthenticationGateway"/> — wraps the real gRPC-Web client proxy. Catches
/// <see cref="RpcException"/> (the underlying client library's own failure signal, not this platform's
/// choice) and decodes it via Midgard's <see cref="RpcExceptionExtensions.DecodeProblem"/> — the one
/// piece of this that's genuine shared infrastructure, since it only ever touches the wire trailer,
/// never <see cref="AuthenticationResult"/> itself (spec §9.8).
/// </summary>
sealed class WasmAuthenticationGateway(IAuthenticationService authenticationService) : IAuthenticationGateway
{
	public async Task<AuthenticationResult> Login(LoginRequest request)
	{
		try
		{
			var result = await authenticationService.Login(request).ConfigureAwait(false);
			return new AuthenticationResult { Succeeded = result.Succeeded };
		}
		catch (RpcException ex)
		{
			return new AuthenticationResult { Succeeded = false, Errors = ex.DecodeProblem() };
		}
	}

	public async Task<AuthenticationResult> Register(RegisterRequest request)
	{
		try
		{
			await authenticationService.Register(request).ConfigureAwait(false);
			return new AuthenticationResult { Succeeded = true };
		}
		catch (RpcException ex)
		{
			return new AuthenticationResult { Succeeded = false, Errors = ex.DecodeProblem() };
		}
	}

	public async Task<AuthenticationResult> Logout(LogoutRequest request)
	{
		try
		{
			await authenticationService.Logout(request).ConfigureAwait(false);
			return new AuthenticationResult { Succeeded = true };
		}
		catch (RpcException ex)
		{
			return new AuthenticationResult { Succeeded = false, Errors = ex.DecodeProblem() };
		}
	}
}
