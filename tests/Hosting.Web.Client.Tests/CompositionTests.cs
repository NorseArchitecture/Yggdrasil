using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Norse.AuthN.Services;

namespace Norse.Hosting.Web.Client.Tests;

/// <summary>
/// Wired-not-designed composition assertion (Task 14): calls the real generated
/// <c>AddNorseGrpcClients</c> extension emitted into <c>Hosting.Web.Client</c> itself, over a channel
/// that needs no live server to construct, and proves it resolves <see cref="IAuthenticationService"/>
/// -- this fails if the registration is ever deleted from <c>Program.cs</c>'s call site's underlying
/// generated method, not just if the API shape changes.
/// </summary>
public sealed class CompositionTests
{
	[Fact]
	void AddNorseGrpcClients_registers_IAuthenticationService_resolvable_from_the_container()
	{
		ServiceCollection services = new();
		using var channel = GrpcChannel.ForAddress("http://localhost");

		services.AddNorseGrpcClients(channel);

		using var provider = services.BuildServiceProvider();
		var authenticationService = provider.GetService<IAuthenticationService>();

		authenticationService.ShouldNotBeNull();
	}
}
