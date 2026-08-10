using System.ServiceModel;
using Norse.Abstractions.Contracts;

namespace Norse.Hosting.Web.Server.Tests.Parity;

/// <summary>
///     The tri-protocol swoop's gRPC service contract (Task 13) — the WCF attribution vocabulary Futhark
///     honors platform-wide (spec §3), with the naming (<c>I{Context}Service</c>) and return shape
///     (<c>Task&lt;Outcome&lt;T&gt;&gt;</c>/<c>ValueTask&lt;Outcome&lt;T&gt;&gt;</c>) Midgard's
///     <c>ContractDiscovery</c> keys on structurally. Not auto-discovered by the real
///     <c>MapNorseGrpcServices()</c> — this lives in the test compilation, never referenced by
///     <c>Hosting.Web.Server</c>'s own generator run — so the swoop host maps it by hand, mirroring
///     <c>MediatorParityTests.CreateHost</c>'s established pattern for a test-local service.
/// </summary>
[ServiceContract(Name = "grpc.parity.v1.ParityService")]
public interface IParityService
{
	/// <summary>Echoes <paramref name="request" /> back as a <see cref="ParityReport" />, through the real mediator pipeline.</summary>
	ValueTask<Outcome<ParityReport>> EchoAsync(ParityRequest request, CancellationToken cancellationToken = default);
}
