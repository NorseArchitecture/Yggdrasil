using Microsoft.AspNetCore.Identity;

namespace Norse.Hosting.Web.Server.Identity;

/// <summary>
/// Throwing placeholder for <see cref="IUserStore{TUser}"/>. ASP.NET Core Identity's service
/// graph (UserManager, SignInManager, security stamp validators, ...) is resolved eagerly by
/// DI validation in Development, so something has to satisfy <see cref="IUserStore{TUser}"/>
/// even before persistence (EF Core stores) lands in a separate tree. Delete this file and
/// register the real EF store in its place once that tree exists — every member here throws.
/// </summary>
public sealed class PlaceholderUserStore : IUserStore<ApplicationUser>
{
	/// <inheritdoc />
	public void Dispose()
	{
	}

	/// <inheritdoc />
	public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
		throw new NotImplementedException();

	/// <inheritdoc />
	public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
		throw new NotImplementedException();
}
