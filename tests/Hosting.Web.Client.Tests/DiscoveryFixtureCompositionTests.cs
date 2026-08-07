using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Norse.DiscoveryFixture;
using Norse.Hosting.Web.Components;

namespace Norse.Hosting.Web.Client.Tests;

/// <summary>
/// Task 14's proof-by-adding-one, a permanent regression lock: <c>DiscoveryFixture</c> is a minimal
/// RCL referenced only here, never by a host itself, carrying exactly one <see cref="IValidator{T}"/>
/// implementation and one <c>@page</c>-routed component. This asserts the generated client-side
/// discovery (Midgard's <c>Infrastructure.Web.Client.Generator</c>, re-run over THIS project's own
/// compilation per the csproj's <c>NorseGeneratorRef</c> whitelist) registers the fixture's validator
/// and includes the fixture's assembly in <see cref="RoutesAdditionalAssemblies"/> — proving
/// "adding a validator/component assembly requires zero host registration edits" against a real,
/// separately compiled consumer, not just the two hosts the generator was written for. Exactly the
/// cross-compilation behavior a snapshot test of the emitter's output (Midgard's own
/// <c>ClientComponentRegistrationEmitterTests</c>) can't catch.
/// </summary>
public sealed class DiscoveryFixtureCompositionTests
{
	[Fact]
	void AddNorseClientComponents_registers_the_fixture_validator_and_its_routable_assembly()
	{
		ServiceCollection services = new();

		// Fully qualified, not services.AddNorseClientComponents(): this project's own compilation
		// carries TWO same-named, same-signature extension methods once the generator runs here too —
		// this one (namespace Norse.Hosting.Web.Client.Tests) and the one already compiled into
		// Hosting.Web.Client.dll (namespace Norse.Hosting.Web.Client, an ancestor of this file's own
		// namespace and therefore implicitly in scope with no using needed). An unqualified call is
		// ambiguous (CS0121); naming the exact generated type sidesteps overload resolution entirely.
		Norse.Hosting.Web.Client.Tests.NorseClientComponentRegistration.AddNorseClientComponents(services);

		using var provider = services.BuildServiceProvider();
		var validator = provider.GetService<IValidator<FixtureRequest>>();
		var additionalAssemblies = provider.GetService<RoutesAdditionalAssemblies>();

		validator.ShouldNotBeNull();
		validator.ShouldBeOfType<FixtureRequestValidator>();
		additionalAssemblies.ShouldNotBeNull();
		additionalAssemblies.Assemblies.ShouldContain(typeof(FixtureRequest).Assembly);
	}
}
