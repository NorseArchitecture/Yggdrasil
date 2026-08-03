using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.AspNetCore.TestHost;

namespace Norse.Hosting.Web.Server.Tests.Swoop;

/// <summary>
/// The lexical corpus test (spec §15) — a shared accepted/rejected lexeme set per §7 scalar row,
/// asserted identical across both text channels against the live <see cref="SwoopHostFixture.App"/>.
/// Every case starts from a fully valid <see cref="TriProtocolSwoopTests"/>-shaped request and
/// overrides exactly one field's raw wire text, so an accept/reject verdict is attributable to that one
/// lexeme, never a side effect of a different field. Coverage: all 13 of 13 §7 rows, including the enum
/// row (<see cref="Parity.ParityStatus"/>, live request-side as of Task 11) — governed names only
/// (<c>active</c>/<c>inactive</c>); wrong case, unmapped tokens, and a bare number all reject identically
/// on both channels.
/// </summary>
[Collection(SwoopCollection.Name)]
public sealed class LexicalCorpus(SwoopHostFixture fixture)
{
	// (field name, base JSON value, base XML attribute text) -- the same field-name vocabulary
	// TriProtocolSwoopTests.JsonBody/XmlBody use, kept here as the one editable base row set both
	// channel-body builders below project from.
	static readonly (string Name, JsonNode Json, string Xml)[] _baseFields =
	[
		("isActive", true, "true"),
		("count", 42, "42"),
		("amount", 1234.56m, "1234.56"),
		("ratio", 1.5f, "1.5"),
		("measurement", 2.25d, "2.25"),
		("initial", "A", "A"),
		("name", "Alice", "Alice"),
		("identifier", "0b917371-0000-0000-0000-000000000001", "0b917371-0000-0000-0000-000000000001"),
		("timestamp", "2026-08-01T14:30:00.0000000Z", "2026-08-01T14:30:00.0000000Z"),
		("timestampOffset", "2026-08-01T14:30:00.0000000+02:00", "2026-08-01T14:30:00.0000000+02:00"),
		("effectiveDate", "2026-08-01", "2026-08-01"),
		("startTime", "14:30:00.0000000", "14:30:00.0000000"),
		("duration", "P1DT2H3M4S", "P1DT2H3M4S"),
		("status", "active", "active")
	];

	static string BuildJson(string fieldName, JsonNode? overrideValue)
	{
		JsonObject obj = [];
		foreach (var (name, value, _) in _baseFields)
			obj[name] = name == fieldName ? overrideValue : JsonNode.Parse(value.ToJsonString());
		return obj.ToJsonString();
	}

	static string BuildXml(string fieldName, string overrideValue)
	{
		XElement root = new("parityRequest");
		foreach (var (name, _, xml) in _baseFields)
			root.SetAttributeValue(name, name == fieldName ? overrideValue : xml);
		return new XDocument(root).ToString();
	}

	async Task<HttpStatusCode> PostAsync(string body, string mediaType, CancellationToken cancellationToken)
	{
		using var client = fixture.App.GetTestClient();
		using var request = new HttpRequestMessage(HttpMethod.Post, "/api/parity") { Content = new StringContent(body, Encoding.UTF8, mediaType) };
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
		using var response = await client.SendAsync(request, cancellationToken);
		return response.StatusCode;
	}

	async Task AssertSameVerdictAsync(string fieldName, JsonNode jsonValue, string xmlValue, bool expectAccepted, CancellationToken cancellationToken)
	{
		var jsonStatus = await PostAsync(BuildJson(fieldName, jsonValue), "application/json", cancellationToken);
		var xmlStatus = await PostAsync(BuildXml(fieldName, xmlValue), "application/xml", cancellationToken);

		var expected = expectAccepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest;
		jsonStatus.ShouldBe(expected, $"JSON channel for field '{fieldName}' lexeme '{jsonValue}'");
		xmlStatus.ShouldBe(expected, $"XML channel for field '{fieldName}' lexeme '{xmlValue}'");
	}

	public static TheoryData<string, JsonNode, string, bool> AcceptedAndRejectedLexemes()
	{
		TheoryData<string, JsonNode, string, bool> data = new()
		{
			// decimal (§7 row: decimal)
			{ "amount", -1234.56m, "-1234.56", true },
			{ "amount", "not-a-decimal", "not-a-decimal", false },

			// float / double (§7 row: float/double) -- non-finite spellings rejected on both channels.
			{ "ratio", "NaN", "NaN", false },
			{ "ratio", "Infinity", "Infinity", false },
			{ "ratio", "-Infinity", "-Infinity", false },
			{ "measurement", "NaN", "NaN", false },
			{ "measurement", "Infinity", "Infinity", false },
			{ "measurement", "-Infinity", "-Infinity", false },

			// Guid (§7 row: Guid) -- lowercase hyphenated accepted; malformed rejected.
			{ "identifier", "11111111-1111-1111-1111-111111111111", "11111111-1111-1111-1111-111111111111", true },
			{ "identifier", "not-a-guid", "not-a-guid", false },

			// DateOnly (§7 row: DateOnly)
			{ "effectiveDate", "2026-01-15", "2026-01-15", true },
			{ "effectiveDate", "not-a-date", "not-a-date", false },

			// TimeSpan (§7 row: TimeSpan, ISO 8601 duration)
			{ "duration", "PT30M", "PT30M", true },
			{ "duration", "not-a-duration", "not-a-duration", false },

			// bool (§7 row: bool)
			{ "isActive", false, "false", true },
			{ "isActive", "not-a-boolean", "not-a-boolean", false },

			// integral (§7 row: integral types)
			{ "count", -7, "-7", true },
			{ "count", "not-a-number", "not-a-number", false },

			// char (§7 row: char) -- exactly one character; anything longer is malformed.
			{ "initial", "Z", "Z", true },
			{ "initial", "ZZ", "ZZ", false },

			// string (§7 row: string) -- verbatim (channel-escaped) wire form: every present lexeme is
			// content, including the empty string (present-empty is distinct from absent, spec §8.2), so
			// this row has accepted cases only -- no rejectable lexeme exists. The XML side proves the
			// channel-escaping half: BuildXml routes the raw text through XElement, so '&' rides as
			// '&amp;' on the wire and still lands verbatim.
			{ "name", "Æsir & Vanir", "Æsir & Vanir", true },
			{ "name", "", "", true },

			// DateTime (§7 row: DateTime, "O" round-trip)
			{ "timestamp", "2026-01-15T08:00:00.0000000Z", "2026-01-15T08:00:00.0000000Z", true },
			{ "timestamp", "not-a-timestamp", "not-a-timestamp", false },

			// DateTimeOffset (§7 row: DateTimeOffset, "O" round-trip)
			{ "timestampOffset", "2026-01-15T08:00:00.0000000-05:00", "2026-01-15T08:00:00.0000000-05:00", true },
			{ "timestampOffset", "not-an-offset", "not-an-offset", false },

			// TimeOnly (§7 row: TimeOnly, "O") -- the rejected lexeme is shape-valid but value-invalid
			// (there is no hour 25), sharper than a plain not-a-time string.
			{ "startTime", "08:15:30.0000000", "08:15:30.0000000", true },
			{ "startTime", "25:00:00.0000000", "25:00:00.0000000", false },

			// enum (§7 row: ParityStatus, live request-side as of Task 11) -- governed names only;
			// wrong case, an unmapped token, and a bare number all reject identically on both channels.
			{ "status", "inactive", "inactive", true },
			{ "status", "Active", "Active", false },
			{ "status", "99", "99", false },
			{ "status", "not-a-status", "not-a-status", false }
		};
		return data;
	}

	[Theory]
	[MemberData(nameof(AcceptedAndRejectedLexemes))]
	async Task Shared_lexeme_verdict_is_identical_on_both_text_channels(string fieldName, JsonNode jsonValue, string xmlValue, bool expectAccepted)
	{
		await AssertSameVerdictAsync(fieldName, jsonValue, xmlValue, expectAccepted, TestContext.Current.CancellationToken);
	}
}
