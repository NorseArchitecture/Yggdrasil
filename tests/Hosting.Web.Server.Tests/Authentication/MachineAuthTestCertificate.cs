using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Norse.Hosting.Web.Server.Tests.Authentication;

/// <summary>
///     The one self-signed cert every composition test in this project needs for OpenIddict to start --
///     generated once per test-assembly load, mirroring how <see cref="TestHostEnvironment" /> fakes
///     connection strings the same way. Empty PFX password, same rationale as the AppHost's own
///     <c>OidcSigningCertificateParameterDefault</c> (Bifröst): the value is already env-var-scoped to
///     this test process, and a second secret protecting it adds nothing.
/// </summary>
static class MachineAuthTestCertificate
{
	/// <summary>The base64-encoded, empty-password PFX -- set as <c>OIDC_SIGNING_CERT_PFX</c> for tests that boot the real <c>Program.cs</c>.</summary>
	internal static readonly string Base64Pfx = ExportFresh();

	static string ExportFresh()
	{
		using var certificate = CreateFresh();
		return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, password: string.Empty));
	}

	/// <summary>
	///     A freshly generated certificate object -- for Task 11's bespoke fixture, which calls
	///     <c>AddNorseAuthenticationService</c> directly and needs an <see cref="X509Certificate2" />, not
	///     the base64 form <see cref="Base64Pfx" /> exists for.
	/// </summary>
	internal static X509Certificate2 CreateFresh()
	{
		using var rsa = RSA.Create(2048);
		var request = new CertificateRequest("CN=Norse Composition Tests", rsa, HashAlgorithmName.SHA256,
			RSASignaturePadding.Pkcs1);
		return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
	}
}
