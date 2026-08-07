using Norse.Abstractions.Contracts;
using Norse.Hosting.Web.Server.Tests.Parity;
using Norse.Infrastructure.Web.Grpc;
using ProtoBuf.Meta;

[assembly: Xunit.AssemblyFixture(typeof(Norse.Hosting.Web.Server.Tests.WireModelFixture))]

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
/// Every <c>RuntimeTypeModel.Default</c> registration this assembly's test hosts need, done exactly
/// once, guaranteed complete before any test runs -- <see cref="Xunit.AssemblyFixtureAttribute"/>
/// initializes the fixture before any test in the assembly is run, ahead of xUnit ever scheduling a
/// (possibly parallel) test collection.
/// </summary>
/// <remarks>
/// Supersedes three prior fix waves that all targeted the wrong layer. Midgard's
/// <c>WireModelRegistrationGuard</c>/NORSE080 (PR #61) made repeated <em>registration</em> calls safe
/// against each other -- a real fix, but for a registration-vs-registration race. This project then hit
/// the same shape twice more: a <c>[ModuleInitializer]</c> warmup (PR #162, deleted by this fixture) and
/// a <see cref="Norse.Hosting.Web.Server.Tests.Swoop.SwoopHostFixture"/>-local retrofit onto the guard
/// (PR #164). Both still left every wire-touching test class an independent, implicitly-parallel xUnit
/// collection -- <c>SwoopHostFixture</c> serialized Swoop's own three classes against each other, but did nothing for
/// <c>MediatorParityTests</c>/<c>WirePathAuthorizationTests</c>/<c>CompositionTests</c>/
/// <c>CountryLookupE2ETests</c> racing it from their own, unguarded collections. PR #166 proved it: a
/// diff that touched nothing but an Asgard version bump still failed <c>MediatorParityTests</c>, on a
/// Midgard version that already carried the PR #61 guard. The guard was never the gap -- registration
/// completing while a wholly unrelated, concurrently-scheduled collection's real gRPC traffic is already
/// in flight is not a race any per-callsite guard closes. Doing every registration here, once, before
/// xUnit schedules anything, closes it at the root: no test collection ever observes a half-registered
/// model, so every collection is free to run in parallel again.
/// </remarks>
public sealed class WireModelFixture
{
	public WireModelFixture()
	{
		var model = RuntimeTypeModel.Default;

		NorseGrpcServerRegistration.RegisterNorseOutcomeSurrogates();
		IdentifierSerializers.Register(model);
		ResultSerializers.Register(model);

		// Outcome<ParityReport> is test-local (Swoop's ParityService is never seen by any generator's
		// own compilation), so it rides the same one-time warmup as everything generated instead of a
		// fixture-local registration.
		model.EnsureRegistered(typeof(Outcome<ParityReport>), () =>
		{
			if (!model.IsDefined(typeof(Outcome<ParityReport>)))
				model.Add(typeof(Outcome<ParityReport>), applyDefaultBehaviour: false).SetSurrogate(typeof(ParityReport));
		});
	}
}
