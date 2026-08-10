using System.Reflection;
using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Norse.Hosting.Web.Components.Layout;

namespace Norse.Hosting.Web.Components.Tests;

public sealed class MainLayoutTests : BunitContext
{
	public MainLayoutTests()
	{
		Services.AddFluentUIComponents();
		JSInterop.Mode = JSRuntimeMode.Loose;
		// MainLayout renders NavMenu, whose AuthorizeView needs a cascading auth state.
		AddAuthorization().SetNotAuthorized();
	}

	[Fact]
	void Footer_carries_no_fluentui_or_learn_microsoft_promo_links()
	{
		var component = Render<MainLayout>();

		component.Markup.ShouldNotContain("fluentui-blazor.net");
		component.Markup.ShouldNotContain("learn.microsoft.com");
	}

	[Fact]
	void Footer_shows_the_platform_name_and_the_hosts_informational_version()
	{
		var component = Render<MainLayout>();

		component.Markup.ShouldContain($"Norse Architecture · {ExpectedInformationalVersion()}");
	}

	// Mirrors the resolution MainLayout itself performs (Assembly.GetEntryAssembly(), falling back to
	// its own assembly only when null), trimmed at '+' to drop build metadata — verifies behavior, not
	// a hard-coded version string.
	static string ExpectedInformationalVersion()
	{
		var assembly = Assembly.GetEntryAssembly() ?? typeof(MainLayout).Assembly;
		var informationalVersion =
			assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

		if (string.IsNullOrEmpty(informationalVersion))
			return "unknown";

		var buildMetadataIndex = informationalVersion.IndexOf('+');
		return buildMetadataIndex < 0 ?
			informationalVersion :
			informationalVersion[..buildMetadataIndex];
	}
}
