using System.Runtime.CompilerServices;
using Norse.Hosting.Web.Server.Tests.Authentication;

namespace Norse.Hosting.Web.Server.Tests;

static class TestHostEnvironment
{
	[ModuleInitializer]
	internal static void Initialize()
	{
		Environment.SetEnvironmentVariable(
			"ConnectionStrings__norse_identity",
			"Host=localhost;Database=norse_identity_composition_tests;Username=test;Password=test");
		Environment.SetEnvironmentVariable(
			"ConnectionStrings__norse_reference",
			"Host=localhost;Database=norse_reference_composition_tests;Username=test;Password=test");
		Environment.SetEnvironmentVariable("OIDC_SIGNING_CERT_PFX", MachineAuthTestCertificate.Base64Pfx);
		// The stranded-account verification would otherwise run at every Program.cs boot here, against a database most
		// of these hosts never reach. The one fact that asserts it turns it back on with a host setting.
		Environment.SetEnvironmentVariable("Identity__Verification__Enabled", "false");
	}
}
