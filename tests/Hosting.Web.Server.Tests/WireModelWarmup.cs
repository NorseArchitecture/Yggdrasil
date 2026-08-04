using System.Runtime.CompilerServices;

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
/// Completes the <c>RuntimeTypeModel.Default</c> wire registration before any test thread makes a
/// call. The generated <c>RegisterNorseOutcomeSurrogates</c> guard is flag-first and non-blocking:
/// a parallel collection's first wire call can observe the flag already set while registration is
/// still mid-flight on another thread and serialize against a half-built model — transport-level
/// failures with no Norse trailers (uniform <c>Fault</c>, null correlation id; observed live on the
/// x64 CI runner 2026-08-03, invisible under arm64-local timing). A module initializer runs
/// single-threaded before xUnit spins up any collection, so every later call is a no-op against a
/// complete model. The emitter-side cure (a blocking guard) is tracked in Glitnir as the real fix.
/// </summary>
static class WireModelWarmup
{
	[ModuleInitializer]
	internal static void Initialize() =>
		NorseGrpcServerRegistration.RegisterNorseOutcomeSurrogates();
}
