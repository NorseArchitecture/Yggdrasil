using Norse.Abstractions.Components.Primitives;
using Norse.Hosting.Web.Components.Layout;

namespace Norse.Hosting.Web.Components;

/// <summary>
/// This host's <see cref="IAppShellLayout"/> registration: <see cref="MainLayout"/> is the app shell
/// downstream RCLs (e.g. Himinbjorg's ManageLayout) nest inside via <c>LayoutView</c>, without those
/// RCLs taking a build-time reference to this project.
/// </summary>
public sealed class AppShellLayout : IAppShellLayout
{
	/// <inheritdoc />
	public Type LayoutType => typeof(MainLayout);
}
