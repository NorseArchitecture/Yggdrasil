using System.Security.Claims;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Norse.Abstractions.Components.Authorization;
using Norse.Abstractions.Components.Primitives;
using Norse.AuthN.Services;
using Norse.Hosting.Web.Components;
using Norse.Hosting.Web.Components.Layout;

namespace Norse.Hosting.Web.Client.Tests;

/// <summary>
///     Wired-not-designed composition assertion (Task 14): calls the real generated
///     <c>AddNorseGrpcClients</c> extension emitted into <c>Hosting.Web.Client</c> itself, over a channel
///     that needs no live server to construct, and proves it resolves <see cref="IAuthenticationService" />
///     -- this fails if the registration is ever deleted from <c>Program.cs</c>'s call site's underlying
///     generated method, not just if the API shape changes. The second fact does the same for the
///     generated <c>AddNorsePolicies</c> (Train 1): the client-side policy registration resolves
///     <c>AuthNPolicies.Account</c> from <c>IAuthorizationPolicyProvider</c>.
/// </summary>
public sealed class CompositionTests
{
	[Fact]
	void AddNorseGrpcClients_registers_IAuthenticationService_resolvable_from_the_container()
	{
		ServiceCollection services = new();
		using var channel = GrpcChannel.ForAddress("http://localhost");

		// Fully qualified, not services.AddNorseGrpcClients(channel): DiscoveryFixtureCompositionTests'
		// NorseGeneratorRef whitelist (Hosting.Web.Client.Tests.csproj) makes the client-side generator
		// run again over THIS project's own compilation too, minting a second
		// Norse.Hosting.Web.Client.Tests.NorseGrpcClientRegistration alongside the real one already
		// compiled into Hosting.Web.Client.dll (Norse.Hosting.Web.Client). An unqualified call resolves
		// to the nearer one (this project's own copy) with no compiler error or warning -- C#'s
		// extension-method lookup walks outward from the innermost enclosing namespace and stops at the
		// first non-empty candidate set, it does not consider both and reject as ambiguous. This test's
		// entire point is asserting the REAL host's registration, so it has to name that copy explicitly
		// or it silently stops testing what its own doc comment above claims it tests -- confirmed via IL
		// inspection (call ... Norse.Hosting.Web.Client.Tests.NorseGrpcClientRegistration::AddNorseGrpcClients
		// bound before this fix, not Norse.Hosting.Web.Client's).
		Client.NorseGrpcClientRegistration.AddNorseGrpcClients(services, channel);

		using var provider = services.BuildServiceProvider();
		var authenticationService = provider.GetService<IAuthenticationService>();

		authenticationService.ShouldNotBeNull();
	}

	[Fact]
	void AddNorseGrpcClients_registers_IAccountService_resolvable_from_the_container()
	{
		ServiceCollection services = new();
		using var channel = GrpcChannel.ForAddress("http://localhost");

		// Fully qualified for the same reason as the IAuthenticationService fact above.
		Client.NorseGrpcClientRegistration.AddNorseGrpcClients(services, channel);

		using var provider = services.BuildServiceProvider();
		var accountService = provider.GetService<IAccountService>();

		accountService.ShouldNotBeNull();
	}

	[Fact]
	void AddNorseAppShellLayout_registers_the_main_layout_as_the_app_shell()
	{
		// Heimdall's ManageLayout injects IAppShellLayout to nest inside the host's chrome; a WASM render
		// of a Manage page without the registration throws at activation. Both hosts' Program.cs call this
		// one extension, so the client registers exactly what the server does.
		ServiceCollection services = new();
		services.AddNorseAppShellLayout();

		using var provider = services.BuildServiceProvider();
		var appShell = provider.GetService<IAppShellLayout>();

		appShell.ShouldNotBeNull();
		appShell.LayoutType.ShouldBe(typeof(MainLayout));
	}

	[Fact]
	async Task AddNorsePolicies_registers_the_account_policy_on_the_client()
	{
		ServiceCollection services = new();
		services.AddLogging().AddAuthorizationCore();
		Client.NorsePolicyRegistration.AddNorsePolicies(services);
		using var provider = services.BuildServiceProvider();
		var policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(AuthNPolicies.Account);
		policy.ShouldNotBeNull();
		var evaluator = provider.GetRequiredService<IAuthorizationService>();
		ClaimsPrincipal anonymous = new(new ClaimsIdentity([new Claim(ClaimTypes.Role, NorseRoles.Anonymous)], "Test"));
		(await evaluator.AuthorizeAsync(anonymous, null, AuthNPolicies.Account)).Succeeded.ShouldBeFalse();
	}

	// The browser only ever holds the cookie identity (spec §7.3), so the WASM mirror of the policies treats its
	// principal as the identity lane: the generated client AddNorsePolicies(), bootstrapped over
	// AddNorseClientAuthorization() with nothing else registered, decides for a signed-in cookie principal exactly as
	// before the lanes existed, and still refuses the anonymous one where it always did.
	[Fact]
	async Task Wasm_policy_evaluation_is_unchanged_for_the_cookie_principal()
	{
		ServiceCollection services = new();
		services.AddLogging();
		Client.NorsePolicyRegistration.AddNorsePolicies(services);
		using var provider = services.BuildServiceProvider();
		var evaluator = provider.GetRequiredService<IAuthorizationService>();
		ClaimsPrincipal signedIn = new(new ClaimsIdentity(
			[new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()), new Claim(ClaimTypes.Role, "user")],
			"Identity.Application"));
		ClaimsPrincipal anonymous = new(new ClaimsIdentity(
			[new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()), new Claim(ClaimTypes.Role, NorseRoles.Anonymous)],
			"Norse.Anonymous"));

		foreach (var policy in new[]
			{
				AuthNPolicies.Public, AuthNPolicies.Account, AuthNPolicies.AccountRead, IdentityPolicies.Self,
				NorsePolicies.Anonymous
			})
			(await evaluator.AuthorizeAsync(signedIn, null, policy)).Succeeded.ShouldBeTrue(policy);

		(await evaluator.AuthorizeAsync(anonymous, null, AuthNPolicies.Public)).Succeeded.ShouldBeTrue();
		(await evaluator.AuthorizeAsync(anonymous, null, AuthNPolicies.Account)).Succeeded.ShouldBeFalse();
		(await evaluator.AuthorizeAsync(signedIn, null, IdentityPolicies.MaskedDisclosure)).Succeeded.ShouldBeFalse();
	}
}
