using System.Runtime.CompilerServices;

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
/// Completes the <c>RuntimeTypeModel.Default</c> wire registration before any test thread makes a
/// call. The generated <c>RegisterNorseOutcomeSurrogates</c> guard is now genuinely blocking — it
/// delegates to <c>Norse.Infrastructure.Web.Grpc.WireModelRegistrationGuard.EnsureRegistered</c>
/// (Midgard, Tasks 4/5 of the wire model registration guard effort), so a parallel collection's
/// first wire call can no longer observe a half-built model the way the old flag-first guard let it
/// (transport-level failures with no Norse trailers — uniform <c>Fault</c>, null correlation id;
/// observed live on the x64 CI runner 2026-08-03, invisible under arm64-local timing). This
/// <see cref="ModuleInitializerAttribute"/> warmup is therefore likely redundant defensive
/// belt-and-suspenders now rather than a load-bearing fix — left in place deliberately; removing it
/// is a separate decision for whoever manages the Midgard-version bump this depends on.
/// </summary>
static class WireModelWarmup
{
	[ModuleInitializer]
	internal static void Initialize() =>
		NorseGrpcServerRegistration.RegisterNorseOutcomeSurrogates();
}
