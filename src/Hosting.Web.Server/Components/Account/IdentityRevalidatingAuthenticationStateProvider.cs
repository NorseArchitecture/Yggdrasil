using Norse.Hosting.Web.Server.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Norse.Hosting.Web.Server.Components.Account;

// This is a server-side AuthenticationStateProvider that revalidates the security stamp for the connected user
// every 30 minutes an interactive circuit is connected.
sealed class IdentityRevalidatingAuthenticationStateProvider(
		ILoggerFactory loggerFactory,
		IServiceScopeFactory scopeFactory,
		IOptions<IdentityOptions> options)
	: RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
	/// <inheritdoc />
	protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

	/// <inheritdoc />
	protected override async Task<bool> ValidateAuthenticationStateAsync(
		AuthenticationState authenticationState, CancellationToken cancellationToken)
	{
		// Get the user manager from a new scope to ensure it fetches fresh data
		var scope = scopeFactory.CreateAsyncScope();
		await using var _ = scope.ConfigureAwait(false);
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
		return await ValidateSecurityStampAsync(userManager, authenticationState.User).ConfigureAwait(false);
	}

	async Task<bool> ValidateSecurityStampAsync(UserManager<ApplicationUser> userManager, ClaimsPrincipal principal)
	{
		var user = await userManager.GetUserAsync(principal).ConfigureAwait(false);
		if (user is null)
		{
			return false;
		}
		else if (!userManager.SupportsUserSecurityStamp)
		{
			return true;
		}
		else
		{
			var principalStamp = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
			var userStamp = await userManager.GetSecurityStampAsync(user).ConfigureAwait(false);
			return principalStamp == userStamp;
		}
	}
}
