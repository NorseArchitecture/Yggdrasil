using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Norse.Hosting.Web.Components.Layout;

namespace Norse.Hosting.Web.Components.Tests;

public sealed class NavMenuTests : BunitContext
{
	public NavMenuTests()
	{
		Services.AddFluentUIComponents();
		// FluentUI components make JS interop calls bunit has no way to know about in advance — loose
		// mode is bunit's own documented answer, rather than hand-enumerating every internal call
		// FluentUI might make (mirrors Heimdall's AuthN.Components.FluentUI.Tests/LoginTests.cs).
		JSInterop.Mode = JSRuntimeMode.Loose;
		// NavMenu's AuthorizeView needs a cascading auth state even for the anonymous path.
		AddAuthorization().SetNotAuthorized();
	}

	[Fact]
	void Groups_the_template_demo_pages_under_a_Template_label()
	{
		var component = Render<NavMenu>();

		component.Markup.ShouldContain("Template");
		component.Markup.ShouldContain("Counter");
		component.Markup.ShouldContain("Weather");
		component.Markup.ShouldContain("Auth Required");
	}

	[Fact]
	void Does_not_render_register_or_login_nav_items()
	{
		var component = Render<NavMenu>();

		component.Markup.ShouldNotContain("Account/Register");
		component.Markup.ShouldNotContain("Account/Login");
		component.Markup.ShouldNotContain(">Register<");
		component.Markup.ShouldNotContain(">Login<");
	}
}
