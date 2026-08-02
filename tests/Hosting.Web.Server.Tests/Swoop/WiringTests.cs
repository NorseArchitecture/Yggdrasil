using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;

namespace Norse.Hosting.Web.Server.Tests.Swoop;

/// <summary>
/// The spec §10.4 "wired, not just designed" probes — every assertion hits the live
/// <see cref="SwoopHostFixture.App"/>, never DI inspection, per the Task 13 brief's explicit
/// instruction: removing any one of these registrations from <c>SwoopHostFixture.InitializeAsync</c>
/// must fail this suite.
/// </summary>
[Collection(SwoopCollection.Name)]
public sealed class WiringTests(SwoopHostFixture fixture)
{
	[Fact]
	async Task Probe_1_the_XML_formatters_answer_content_negotiation_for_a_successful_request()
	{
		// If AddNorseXml's formatter pair were never inserted, an "Accept: application/xml" request
		// would 406 (no formatter can produce it) instead of rendering XML -- this is the live-host
		// proof the registration actually took, not merely that the source line exists.
		const string Body = """<?xml version="1.0" encoding="utf-8"?><parityRequest isActive="true" count="1" amount="1" ratio="1" measurement="1" initial="A" name="A" identifier="0b917371-0000-0000-0000-000000000001" timestamp="2026-08-01T00:00:00.0000000Z" timestampOffset="2026-08-01T00:00:00.0000000+00:00" effectiveDate="2026-08-01" startTime="00:00:00.0000000" duration="PT1S" />""";

		using var client = fixture.App.GetTestClient();
		using var request = new HttpRequestMessage(HttpMethod.Post, "/api/parity") { Content = new StringContent(Body, Encoding.UTF8, "application/xml") };
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

		using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		response.Content.Headers.ContentType!.MediaType.ShouldBe("application/xml");
		var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
		text.ShouldStartWith("<?xml");
	}

	[Fact]
	async Task Probe_1b_the_problem_writer_negotiates_for_Accept_application_problem_plus_xml()
	{
		// GrpcControllerBase's class-level [Produces("application/json", "application/xml")] once had a
		// content-negotiation bug that routed a Problem() result to plain application/xml instead of
		// application/problem+xml (Task 10 context) -- this is the live-host re-proof that the fix
		// (explicit ContentTypes on the failure ObjectResult) still holds for this controller.
		using var client = fixture.App.GetTestClient();
		using var request = new HttpRequestMessage(HttpMethod.Post, "/api/parity") { Content = new StringContent("<parityRequest />", Encoding.UTF8, "application/xml") };
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/problem+xml"));

		using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+xml");
	}

	[Fact]
	async Task Probe_2_the_live_OpenAPI_document_renders_parity_contracts_unwrapped_with_no_Outcome_or_Result_names()
	{
		// SwoopHostFixture carries the real AddOpenApi wiring (all three transformers) alongside the real
		// generated Futhark shapes -- once a genuine platform/toolchain blocker (see SwoopHostFixture's
		// remarks), now fixed for real, so this probe can finally do what the brief's Step 1 actually
		// asks: fetch the live document and show ParityRequest/ParityReport rendered unwrapped, not just
		// "the pipeline ran clean with nothing to unwrap."
		using var client = fixture.App.GetTestClient();
		using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);

		var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
		var document = JsonNode.Parse(json)!;

		var schemas = document["components"]?["schemas"]?.AsObject()
			?? throw new InvalidOperationException("document carries no components.schemas");

		schemas.ContainsKey("ParityRequest").ShouldBeTrue();
		schemas.ContainsKey("ParityReport").ShouldBeTrue();

		// Result<DateOnly> (EffectiveDate) unwraps to the bare scalar's schema -- string/date, never the
		// union's own reflected shape (spec §10.1/§12).
		var effectiveDate = schemas["ParityRequest"]!["properties"]!["effectiveDate"]!;
		effectiveDate["type"]!.GetValue<string>().ShouldBe("string");
		effectiveDate["format"]!.GetValue<string>().ShouldBe("date");

		// The symmetry law's own tripwire (UnionLeakGuardTransformer) already throws 500 if a Result/
		// Outcome-named schema ever reaches components.schemas -- this is the belt-and-suspenders text
		// check the brief asks for: the strings themselves appear nowhere in the finished document.
		json.ShouldNotContain("\"Outcome\"");
		schemas.Select(kvp => kvp.Key).Any(IsReservedUnionName).ShouldBeFalse();
	}

	static bool IsReservedUnionName(string schemaName) =>
		schemaName == "Result" || schemaName == "Outcome" ||
		schemaName.StartsWith("ResultOf", StringComparison.Ordinal) ||
		schemaName.StartsWith("OutcomeOf", StringComparison.Ordinal);
}
