using Bunit;
using System.Security.Claims;
using Bunit.TestDoubles;
using Microsoft.FluentUI.AspNetCore.Components;
using Norse.AuthN.Services;
using Norse.Hosting.Web.Components.Layout;

namespace Norse.Hosting.Web.Components.Tests;

public sealed class NavMenuTests : BunitContext
{
	readonly BunitAuthorizationContext _authorization;

	public NavMenuTests()
	{
		Services.AddFluentUIComponents();
		// FluentUI components make JS interop calls bunit has no way to know about in advance — loose
		// mode is bunit's own documented answer, rather than hand-enumerating every internal call
		// FluentUI might make (mirrors Heimdall's AuthN.Components.FluentUI.Tests/LoginTests.cs).
		JSInterop.Mode = JSRuntimeMode.Loose;
		// NavMenu's AuthorizeView needs a cascading auth state even for the anonymous path.
		_authorization = AddAuthorization();
		_authorization.SetNotAuthorized();
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

	// A62: Heimdall's /Account/Logout is a click-to-confirm page, so the nav reaches it by a plain link; the retired
	// POST form targeted Himinbjorg's deleted minimal-API logout endpoint.
	[Fact]
	void Logout_is_a_link_to_heimdalls_logout_page()
	{
		SignIn("user@example.test");

		var component = Render<NavMenu>();

		var logout = component.FindAll("a").Where(static anchor => anchor.TextContent.Trim() == "Logout").ShouldHaveSingleItem();
		logout.GetAttribute("href").ShouldBe("Account/Logout");
		component.FindAll("form").ShouldBeEmpty();
	}

	// The anonymous-cookie principal is authenticated, so a bare AuthorizeView would offer it Manage and Logout;
	// the block is gated on the account tier the pages themselves require.
	[Fact]
	void The_anonymous_principal_is_offered_no_account_block()
	{
		_authorization.SetAuthorized("anonymous");

		var component = Render<NavMenu>();

		component.Markup.ShouldNotContain("Account/Manage");
		component.Markup.ShouldNotContain("Account/Logout");
	}

	[Fact]
	void A_signed_in_account_is_offered_manage_under_its_email()
	{
		SignIn("Display Name");
		_authorization.SetClaims(new Claim(ClaimTypes.Email, "user@example.test"));

		var component = Render<NavMenu>();

		ManageLink(component).TextContent.Trim().ShouldBe("user@example.test");
		component.Markup.ShouldContain("Account/Logout");
	}

	[Fact]
	void A_signed_in_account_without_an_email_is_offered_manage_under_its_name()
	{
		SignIn("Display Name");

		var component = Render<NavMenu>();

		ManageLink(component).TextContent.Trim().ShouldBe("Display Name");
	}

	[Fact]
	void A_signed_in_account_without_an_email_or_a_name_is_offered_manage_under_a_fallback_label()
	{
		SignIn(string.Empty);

		var component = Render<NavMenu>();

		ManageLink(component).TextContent.Trim().ShouldBe("Account");
	}

	void SignIn(string name)
	{
		_authorization.SetAuthorized(name);
		_authorization.SetPolicies(AuthNPolicies.Account);
	}

	static AngleSharp.Dom.IElement ManageLink(IRenderedComponent<NavMenu> component) =>
		component.FindAll("a").Where(static anchor => anchor.GetAttribute("href") == "Account/Manage").ShouldHaveSingleItem();
}
