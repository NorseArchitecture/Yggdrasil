using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Norse.AuthN.Services;

namespace Norse.Hosting.Web.Client.Tests;

/// <summary>
///     Wired-not-designed composition assertion (Task 14): calls the real generated
///     <c>AddNorseGrpcClients</c> extension emitted into <c>Hosting.Web.Client</c> itself, over a channel
///     that needs no live server to construct, and proves it resolves <see cref="IAuthenticationService" />
///     -- this fails if the registration is ever deleted from <c>Program.cs</c>'s call site's underlying
///     generated method, not just if the API shape changes.
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
}
