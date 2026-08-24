using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Norse.Hosting.Web.Server.Tests.Authentication;

[CollectionDefinition("MachineAuthPostgres")]
[SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix",
	Justification = "xUnit collection fixture naming convention")]
public sealed class MachineAuthPostgresCollection : ICollectionFixture<MachineAuthPostgresFixture>;

/// <summary>
///     The end-to-end proof (Himinbjorg#49 spec §7): a real OpenIddict-issued JWT reaches Mímir's
///     <c>CountriesController</c> in both JSON and XML — the wire shape that has never been proven end to
///     end before this story, per the spec's correction of the stale "host-wiring gap" premise (Mímir's
///     own CLAUDE.md line 37, which this plan's Phase 6 corrects).
/// </summary>
[Collection("MachineAuthPostgres")]
public sealed class MachineAuthE2ETests(MachineAuthPostgresFixture fixture)
{
	[Fact]
	async Task A_seeded_client_reaches_the_facade_in_JSON()
	{
		await using var app = await fixture.CreateHostAsync();
		await SeedClientAsync(app.Services, "json-test-client", "json-test-secret");
		using var client = app.GetTestServer().CreateClient();
		// TestServer's default client addresses http://localhost/, but OpenIddict's token endpoint
		// rejects non-HTTPS requests by default (Identity.Web.Server's AddNorseIdentity, deliberately not
		// relaxed via DisableTransportSecurityRequirement() -- same fix as Himinbjorg's own
		// PostgresIdentityFixture.CreateTestClient()). TestServer never opens a real socket, so pointing
		// the client at an https:// base address is enough to make Request.IsHttps true.
		client.BaseAddress = new Uri("https://localhost/");
		var token = await ObtainAccessTokenAsync(client, "json-test-client", "json-test-secret");

		// The actual wire-shape proof, not just "some 200 came back" -- CountryResponse's real fields
		// (Reference.Contracts/CountryResponse.cs, [DataContract]/[DataMember], no explicit wire names, so
		// AddNorseJson's camelCase policy governs) round-trip correctly, including the flags-as-array law
		// (spec's inherited ruling, 2026-08-02-futhark-enum-wire-law-design.md). Afghanistan (AF) is the
		// non-empty case: Mimisbrunnr's committed seed row (seeds/country-or-area.tsv, "004 AF AFG
		// Afghanistan 034 true true false") carries both IsLeastDevelopedCountry and
		// IsLandLockedDevelopingCountry, so its two set bits must decompose into two governed names, in
		// declaration order (EnumLexical.FormatFlags walks the table in member-declaration order,
		// Midgard's WriterEmitter.FlagsDecomposition and its JSON twin FlagsEnumJsonConverter share the
		// same table) -- camelCase governed names, per AddNorseJson's policy and Midgard's own
		// EnumLexicalTests fixture (e.g. ArchiveMode.ReadWrite -> "readWrite").
		using var afResponse = await GetCountryAsync(client, token, "AF", "application/json");
		afResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
		afResponse.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

		await using var afStream =
			await afResponse.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
		using var afDocument =
			await JsonDocument.ParseAsync(afStream, cancellationToken: TestContext.Current.CancellationToken);
		var afRoot = afDocument.RootElement;
		afRoot.GetProperty("alpha2").GetString().ShouldBe("AF");
		afRoot.GetProperty("alpha3").GetString().ShouldBe("AFG");
		afRoot.GetProperty("name").GetString().ShouldNotBeNullOrWhiteSpace();
		var afClassification = afRoot.GetProperty("classification");
		afClassification.ValueKind.ShouldBe(JsonValueKind.Array);
		afClassification.EnumerateArray().Select(static element => element.GetString())
			.ShouldBe(["leastDevelopedCountry", "landLockedDevelopingCountry"]);

		// US holds none of the three UN classification flags -- the zero-flags case stays covered, proven
		// positively (array present, genuinely empty), never by a loop that could vacuously pass without
		// running.
		using var usResponse = await GetCountryAsync(client, token, "US", "application/json");
		usResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
		await using var usStream =
			await usResponse.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
		using var usDocument =
			await JsonDocument.ParseAsync(usStream, cancellationToken: TestContext.Current.CancellationToken);
		var usClassification = usDocument.RootElement.GetProperty("classification");
		usClassification.ValueKind.ShouldBe(JsonValueKind.Array);
		usClassification.GetArrayLength().ShouldBe(0);
	}

	[Fact]
	async Task A_seeded_client_reaches_the_facade_in_XML()
	{
		await using var app = await fixture.CreateHostAsync();
		await SeedClientAsync(app.Services, "xml-test-client", "xml-test-secret");
		using var client = app.GetTestServer().CreateClient();
		// TestServer's default client addresses http://localhost/, but OpenIddict's token endpoint
		// rejects non-HTTPS requests by default (Identity.Web.Server's AddNorseIdentity, deliberately not
		// relaxed via DisableTransportSecurityRequirement() -- same fix as Himinbjorg's own
		// PostgresIdentityFixture.CreateTestClient()). TestServer never opens a real socket, so pointing
		// the client at an https:// base address is enough to make Request.IsHttps true.
		client.BaseAddress = new Uri("https://localhost/");
		var token = await ObtainAccessTokenAsync(client, "xml-test-client", "xml-test-secret");

		// Same wire-shape proof as the JSON fact, over the XML channel -- Program.cs configures
		// AddNorseXml(XmlCaseStyle.CamelCase, ...), so wire names are camelCase, same as the JSON
		// property names. Scalar members ride as attributes on their owning element, never as child
		// elements -- Futhark's canonical writer shape (design spec §6, WriterEmitter.WriteAttribute):
		// declaration-order attributes then child elements, the same law TriProtocolSwoopTests's own
		// XML round trip already proves (root.Attribute("isActive"), etc.). CountryResponse's Alpha2/
		// Alpha3/Name are scalar members, so they land as attributes on the root element -- read via
		// xml.Root rather than a hardcoded root element name, so this still doesn't depend on a
		// namespace/prefix assumption this plan's research didn't independently verify.
		//
		// Afghanistan (AF) is the non-empty case -- Mimisbrunnr's committed seed row
		// (seeds/country-or-area.tsv, "004 AF AFG Afghanistan 034 true true false") carries both
		// IsLeastDevelopedCountry and IsLandLockedDevelopingCountry. Futhark's XML writer
		// (WriterEmitter.WriteFlags/FlagsDecomposition) emits one <classification>governedName</classification>
		// element per set bit, in the table's declaration order, so a real non-empty response must produce
		// exactly those two elements, in that order, with camelCase governed-name text content -- not an
		// empty foreach that would "pass" for any input, including a broken writer.
		using var afResponse = await GetCountryAsync(client, token, "AF", "application/xml");
		afResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
		afResponse.Content.Headers.ContentType!.MediaType.ShouldBe("application/xml");

		var afXml = XDocument.Parse(await afResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
		var afRoot = afXml.Root ?? throw new InvalidOperationException("XML response carried no root element");
		afRoot.Attribute("alpha2")!.Value.ShouldBe("AF");
		afRoot.Attribute("alpha3")!.Value.ShouldBe("AFG");
		afRoot.Attribute("name")!.Value.ShouldNotBeNullOrWhiteSpace();

		var afClassifications = afXml.Descendants().Where(static e => e.Name.LocalName == "classification").ToList();
		afClassifications.ShouldNotBeEmpty();
		afClassifications.Select(static e => e.Value)
			.ShouldBe(["leastDevelopedCountry", "landLockedDevelopingCountry"]);

		// US holds none of the three UN classification flags -- the zero-flags case stays covered, proven
		// positively (the classification descendant sequence itself is empty), never by a foreach whose
		// body could vacuously never run.
		using var usResponse = await GetCountryAsync(client, token, "US", "application/xml");
		usResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
		var usXml = XDocument.Parse(await usResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
		usXml.Descendants().Where(static e => e.Name.LocalName == "classification").ShouldBeEmpty();
	}

	[Fact]
	async Task An_invalid_token_is_rejected_bare()
	{
		await using var app = await fixture.CreateHostAsync();
		using var client = app.GetTestServer().CreateClient();
		// TestServer's default client addresses http://localhost/, but OpenIddict's token endpoint
		// rejects non-HTTPS requests by default (Identity.Web.Server's AddNorseIdentity, deliberately not
		// relaxed via DisableTransportSecurityRequirement() -- same fix as Himinbjorg's own
		// PostgresIdentityFixture.CreateTestClient()). TestServer never opens a real socket, so pointing
		// the client at an https:// base address is enough to make Request.IsHttps true.
		client.BaseAddress = new Uri("https://localhost/");

		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/reference/countries/US");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");

		using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
	}

	[Fact]
	async Task An_expired_token_is_rejected_bare()
	{
		// A one-second lifetime, minted for real through OpenIddict's own issuance path (never a
		// hand-constructed JWT outside it, which would prove nothing about this platform's actual expiry
		// handling) -- CreateHostAsync's accessTokenLifetime override exists for exactly this test.
		await using var app = await fixture.CreateHostAsync(accessTokenLifetime: TimeSpan.FromSeconds(1));
		await SeedClientAsync(app.Services, "expiry-test-client", "expiry-test-secret");
		using var client = app.GetTestServer().CreateClient();
		// TestServer's default client addresses http://localhost/, but OpenIddict's token endpoint
		// rejects non-HTTPS requests by default (Identity.Web.Server's AddNorseIdentity, deliberately not
		// relaxed via DisableTransportSecurityRequirement() -- same fix as Himinbjorg's own
		// PostgresIdentityFixture.CreateTestClient()). TestServer never opens a real socket, so pointing
		// the client at an https:// base address is enough to make Request.IsHttps true.
		client.BaseAddress = new Uri("https://localhost/");
		var token = await ObtainAccessTokenAsync(client, "expiry-test-client", "expiry-test-secret");
		await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

		using var request = new HttpRequestMessage(HttpMethod.Get, "/api/reference/countries/US");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

		using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
	}

	static async Task<HttpResponseMessage> GetCountryAsync(HttpClient client, string token, string alpha2, string accept)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/reference/countries/{alpha2}");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
		return await client.SendAsync(request, TestContext.Current.CancellationToken);
	}

	static async Task SeedClientAsync(IServiceProvider services, string clientId, string secret)
	{
		await using var scope = services.CreateAsyncScope();
		var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
		await manager.CreateAsync(new OpenIddictApplicationDescriptor
		{
			ClientId = clientId,
			ClientSecret = secret,
			ClientType = OpenIddictConstants.ClientTypes.Confidential,
			Permissions =
			{
				OpenIddictConstants.Permissions.Endpoints.Token,
				OpenIddictConstants.Permissions.GrantTypes.ClientCredentials
			}
		}, TestContext.Current.CancellationToken).ConfigureAwait(false);
	}

	static async Task<string> ObtainAccessTokenAsync(HttpClient client, string clientId, string secret)
	{
		using var content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["grant_type"] = "client_credentials",
			["client_id"] = clientId,
			["client_secret"] = secret
		});
		using var response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative),
			content, TestContext.Current.CancellationToken);
		response.EnsureSuccessStatusCode();
		var payload = await response.Content
			.ReadFromJsonAsync<TokenResponse>(TestContext.Current.CancellationToken)
			.ConfigureAwait(false);
		return payload!.AccessToken;
	}

	// Snake_case on the wire (access_token/token_type/expires_in) -- STJ's case-insensitive matching only
	// folds case, never underscores, so these attributes are required, not decorative (same fix as
	// Himinbjörg's OpenIddictTokenEndpointTests, Phase 3 Task 5).
	sealed record TokenResponse(
		[property: JsonPropertyName("access_token")] string AccessToken,
		[property: JsonPropertyName("token_type")] string TokenType,
		[property: JsonPropertyName("expires_in")] int ExpiresIn);
}
