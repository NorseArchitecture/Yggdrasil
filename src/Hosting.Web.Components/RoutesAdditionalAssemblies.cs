using System.Reflection;

namespace Norse.Hosting.Web.Components;

/// <summary>
/// DI-registered source of the host's additional assemblies for <see cref="Routes"/> to scan.
/// Injected rather than passed as a component parameter because parameters crossing an interactive
/// render-mode boundary must be JSON-serializable, and <see cref="Assembly"/> is not.
/// </summary>
public sealed class RoutesAdditionalAssemblies(IEnumerable<Assembly> assemblies)
{
	/// <summary>The assemblies to scan for routable components, beyond <see cref="Routes"/>'s own.</summary>
	public IEnumerable<Assembly> Assemblies { get; } = assemblies;
}
