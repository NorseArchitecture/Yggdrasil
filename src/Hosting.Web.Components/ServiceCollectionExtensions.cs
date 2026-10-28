using Microsoft.Extensions.DependencyInjection;
using Norse.Abstractions.Components.Primitives;

namespace Norse.Hosting.Web.Components;

/// <summary>
///     The shell's one registration, shared by both hosts so the server and the WASM client can never
///     disagree on which layout upstream RCLs (Heimdall's <c>ManageLayout</c>) nest inside.
/// </summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	///     Registers <see cref="AppShellLayout" /> as the singleton <see cref="IAppShellLayout" />. A Manage
	///     page rendered on a host without it fails to activate its layout.
	/// </summary>
	public static IServiceCollection AddNorseAppShellLayout(this IServiceCollection services) =>
		services.AddSingleton<IAppShellLayout, AppShellLayout>();
}
