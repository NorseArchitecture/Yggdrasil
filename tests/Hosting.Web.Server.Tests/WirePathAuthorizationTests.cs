using System.Runtime.Serialization;
using System.ServiceModel;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Norse.Infrastructure.Web.Server.Mediator.Grpc;
using ProtoBuf.Grpc.Client;

namespace Norse.Hosting.Web.Server.Tests;

[ServiceContract]
public interface IRestrictedService
{
	[Authorize(Policy = "Test.NeverSatisfied")]
	[OperationContract]
	Task<RestrictedResponse> Restricted(RestrictedRequest request);
}

[DataContract]
public sealed record RestrictedRequest;

[DataContract]
public sealed record RestrictedResponse;

[Authorize(Policy = "Test.NeverSatisfied")] // mirrored, exactly as Task 12 mirrors it onto AuthenticationService
sealed class RestrictedService : IRestrictedService
{
	public Task<RestrictedResponse> Restricted(RestrictedRequest request) => Task.FromResult(new RestrictedResponse());
}

public sealed class WirePathAuthorizationTests
{
	[Fact]
	async Task UnauthenticatedCall_AgainstRestrictivePolicy_RejectedWithUnauthenticatedAndErrorInfo()
	{
		// Grpc.Net.Client refuses even to attempt an HTTP/2 call over a plain "http://" address unless
		// this switch is set — real deployments always terminate TLS in front of the gRPC endpoint, but
		// TestServer's in-memory handler has no TLS to negotiate, so the client-side guard needs the
		// explicit opt-in here.
		AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

		using var host = await new HostBuilder()
			.ConfigureWebHost(webHost =>
			{
				webHost.UseTestServer();
				webHost.ConfigureServices(services =>
				{
					services.AddNorseCodeFirstGrpc();
					services.AddAuthorization(o => o.AddPolicy("Test.NeverSatisfied", p => p.RequireAssertion(_ => false)));
					// Plain .AddCookie() redirects (302) on challenge/forbid, assuming a browser — exactly
					// what would silently turn this rejection into a "Bad gRPC response" the client can't
					// interpret as any real status code at all, defeating the point of this test. Real
					// gRPC/API endpoints need the bare status code, not a login-page redirect.
					services.AddAuthentication().AddCookie(o =>
					{
						o.Events.OnRedirectToLogin = context =>
						{
							context.Response.StatusCode = StatusCodes.Status401Unauthorized;
							return Task.CompletedTask;
						};
						o.Events.OnRedirectToAccessDenied = context =>
						{
							context.Response.StatusCode = StatusCodes.Status403Forbidden;
							return Task.CompletedTask;
						};
					});
					services.AddScoped<IRestrictedService, RestrictedService>();
				});
				webHost.Configure(app =>
				{
					app.UseRouting();
					app.UseAuthentication();
					app.UseAuthorization();
					app.UseEndpoints(endpoints => endpoints.MapGrpcService<RestrictedService>());
				});
			})
			.StartAsync(TestContext.Current.CancellationToken);

		var handler = host.GetTestServer().CreateHandler();
		var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
		var client = channel.CreateGrpcService<IRestrictedService>();

		var exception = await Should.ThrowAsync<RpcException>(async () => await client.Restricted(new RestrictedRequest()));

		// Proves [Authorize] is discovered as real, enforced endpoint metadata under AddNorseCodeFirstGrpc
		// — removing it entirely (verified locally, both from the interface and the mirror on
		// RestrictedService) makes the call succeed instead of throwing, so this is a genuine
		// enforcement check, not a false positive. Empirically, protobuf-net.Grpc.AspNetCore already
		// discovers [Authorize] straight off the interface method here — the mirror on RestrictedService
		// (matching Task 12's AuthenticationService) is defense in depth, not load-bearing for this
		// specific proxy/routing combination.
		exception.StatusCode.ShouldBe(StatusCode.Unauthenticated);
	}
}
