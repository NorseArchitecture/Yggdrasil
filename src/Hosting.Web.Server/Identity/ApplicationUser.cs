using Microsoft.AspNetCore.Identity;

namespace Norse.Hosting.Web.Server.Identity;

/// <summary>
/// The application's Identity user. Persistence (EF Core stores, migrations) lands in a
/// separate tree — this type exists so <see cref="UserManager{TUser}"/>/
/// <see cref="SignInManager{TUser}"/> and the Identity Razor components have a concrete
/// user to compile against in the meantime.
/// </summary>
public sealed class ApplicationUser : IdentityUser;
