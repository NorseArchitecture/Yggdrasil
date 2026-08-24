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
	}
}
