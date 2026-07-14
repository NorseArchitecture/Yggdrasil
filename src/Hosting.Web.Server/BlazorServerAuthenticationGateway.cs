using Norse.Abstractions.Web.Server.Mediator;
using Norse.AuthN.Components;
using Norse.Identity;
using Norse.Infrastructure.Web.Server.DeferredSignIn;

namespace Norse.Hosting.Web.Server;

/// <summary>
/// Blazor Server's own <see cref="IAuthenticationGateway"/> — calls the mediator handlers directly,
/// in-process, no gRPC involved at all (per §2's transport matrix). Maps <c>Outcome&lt;T&gt;</c> to
/// <see cref="AuthenticationResult"/> inline — this glue is realm-specific, not generic Midgard
/// infrastructure (spec §9.8).
/// </summary>
sealed class BlazorServerAuthenticationGateway(
	IRequestHandler<LoginRequest, Outcome<BoolResponse>> loginHandler,
	IRequestHandler<RegisterRequest, Outcome<BoolResponse>> registerHandler,
	IRequestHandler<LogoutRequest, Outcome> logoutHandler,
	IHttpContextAccessor httpContextAccessor)
	: IAuthenticationGateway
{
	public async Task<AuthenticationResult> Login(LoginRequest request)
	{
		var outcome = await loginHandler.Handle(request, httpContextAccessor.HttpContext!.RequestAborted).ConfigureAwait(false);
		if (!outcome.IsSuccess)
			return new AuthenticationResult { Succeeded = false, Errors = outcome.Problem!.Errors };

		return new AuthenticationResult { Succeeded = outcome.Value!.Value, DeferredCompletionUrl = TryGetDeferredCompletionUrl() };
	}

	public async Task<AuthenticationResult> Register(RegisterRequest request)
	{
		var outcome = await registerHandler.Handle(request, httpContextAccessor.HttpContext!.RequestAborted).ConfigureAwait(false);
		return new AuthenticationResult { Succeeded = outcome.IsSuccess, Errors = outcome.Problem?.Errors ?? new Dictionary<string, string[]>() };
	}

	public async Task<AuthenticationResult> Logout(LogoutRequest request)
	{
		var outcome = await logoutHandler.Handle(request, httpContextAccessor.HttpContext!.RequestAborted).ConfigureAwait(false);
		return new AuthenticationResult { Succeeded = outcome.IsSuccess, DeferredCompletionUrl = TryGetDeferredCompletionUrl() };
	}

	string? TryGetDeferredCompletionUrl()
	{
		if (httpContextAccessor.HttpContext!.Items[NorseSignInManager.DeferredSignInKeyItemName] is not string key)
			return null;

		return $"{DeferredSignInEndpointRouteBuilderExtensions.DefaultPattern}?key={Uri.EscapeDataString(key)}&returnUrl={Uri.EscapeDataString("/")}";
	}
}
