using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json; // Authored deliberately: the SDK's implicit using is removed platform-wide (NORSE070 carrier); tests are law-exempt and re-add it explicitly.
using System.Security.Claims;
using System.Text;
using FluentValidation;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Norse.Abstractions.Contracts;
using Norse.Abstractions.Web.Server.Mediator;
using Norse.Hosting.Web.Server.Tests.Parity;
using Norse.Infrastructure.Web.Client.Grpc;
using Norse.Infrastructure.Web.Grpc;
using Norse.Infrastructure.Web.Server.Json;
using Norse.Infrastructure.Web.Server.Mediator;
using Norse.Infrastructure.Web.Server.Mediator.Grpc;
using Norse.Infrastructure.Web.Server.OpenApi;
using Norse.Infrastructure.Web.Server.Xml;
using Norse.Primitives;
using ProtoBuf.Grpc.Client;
using ProtoBuf.Meta;

namespace Norse.Hosting.Web.Server.Tests.Swoop;

/// <summary>
/// The tri-protocol swoop's shared live host (spec §15, Task 13 Step 1) — the one place gRPC, REST-JSON,
/// REST-XML, and the OpenAPI document are all wired onto the SAME <see cref="ParityController"/>/
/// <see cref="ParityService"/> pair, hand-registered exactly as the real registration generators would
/// emit it (mirrors <c>MediatorParityTests.CreateHost</c>) since <see cref="ParityService"/> is
/// test-local, never referenced by any generator's own compilation. Hits the real formatters, the real
/// mediator pipeline, the real <c>NorseXmlShapeRegistration.Build()</c> generator output, and (for the
/// gRPC leg) the real <c>Result&lt;T&gt;</c>/<c>Outcome&lt;T&gt;</c> protobuf surrogates.
/// </summary>
/// <remarks>
/// An earlier revision of this fixture carried a hand-written <c>ParityXmlShapes</c> stand-in instead of
/// the generated registration, after an isolated repro appeared to show the Xml shape generator being
/// starved of output whenever a second Roslyn generator (including <c>AddOpenApi</c>'s own bundled
/// <c>XmlCommentGenerator</c>) shared the compilation. That repro was flawed — it never had Midgard's
/// package-bundling path wired, so it was never actually exercising two generators against a correctly
/// packaged Xml generator in the first place. The real root cause, found independently and fixed for
/// real: <c>Infrastructure.Web.Server.csproj</c> never bundled the Xml generator into its shipped
/// package at all (a gap Task 5's own report flagged and nobody picked up), and Bifröst's dev-mode
/// forwarding had no way to name a second generator against one <c>NorseRef</c> identity. Both are fixed
/// — Midgard commit <c>a1c1a87</c> (package bundling) and Bifröst's new <c>NorseGeneratorRef</c> item
/// (Gap 4, <c>../Glitnir/docs/Platform/specs/2026-07-01-norseref-generator-forwarding-design.md</c>) —
/// and this fixture now calls the real generated <c>NorseXmlShapeRegistration.Build()</c> and carries
/// real <c>AddOpenApi</c>/<c>MapOpenApi</c> wiring, verified working with Midgard's Xml generator and
/// Microsoft's own bundled <c>XmlCommentGenerator</c> both active in this compilation simultaneously —
/// the exact pairing the earlier, flawed repro claimed was impossible.
/// </remarks>
public sealed class SwoopHostFixture : IAsyncLifetime
{
	// Blocking, not check-then-act: this fixture is constructed once per test CLASS (IClassFixture),
	// and xUnit runs different classes' fixtures concurrently by default -- two SwoopHostFixture
	// instances racing IsDefined/Add against the shared RuntimeTypeModel.Default is the identical
	// TOCTOU shape Midgard's own guards were just hardened against
	// (../../../../Glitnir/docs/Midgard/2026-08-03-surrogate-guard-race-filing.md), just hand-rolled
	// here instead of behind IdentifierSerializers/ResultSerializers. A second racing caller used to
	// observe IsDefined() still false and call Add() again, throwing "type already added" mid-registration
	// and leaving the shared model in whatever partial state protobuf-net's own Add() left it in --
	// exactly the failure class this platform has already chased once.
	static readonly Lazy<bool> _parityReportSurrogateRegistered = new(() =>
	{
		var model = RuntimeTypeModel.Default;
		if (!model.IsDefined(typeof(Outcome<ParityReport>)))
			model.Add(typeof(Outcome<ParityReport>), applyDefaultBehaviour: false).SetSurrogate(typeof(ParityReport));
		return true;
	}, LazyThreadSafetyMode.ExecutionAndPublication);

	public WebApplication App { get; private set; } = null!;

	public async ValueTask InitializeAsync()
	{
		// TestServer's in-memory handler has no TLS to negotiate -- mirrors CountryLookupE2ETests/
		// MediatorParityTests/WirePathAuthorizationTests' identical opt-in for a plain "http://" gRPC call.
		AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

		var model = RuntimeTypeModel.Default;
		IdentifierSerializers.Register(model);
		// Result<T> has no automatic wire law anywhere in the generated wiring -- AddNorseCodeFirstGrpc()
		// only registers the interceptor stack + health checks (verified directly against its source,
		// Task 13 research) -- so a real host composing a Result<T>-bearing gRPC contract (ParityRequest)
		// must call this itself. No prior task's own composition root does this because no prior facade
		// controller's request type crossed the gRPC leg carrying Result<T> members until now.
		// ResultSerializers.Register also carries the general wire law for bare (non-Result-wrapped)
		// DateTimeOffset fields (Midgard's DateTimeOffsetSerializer) -- the Task 13 cross-task finding
		// that used to force a test-local stopgap serializer here, fixed for real in
		// Infrastructure.Web.Grpc in the postmortem gap pass: ParityReport.TimestampOffset (response
		// scalars never wrap, spec §5.4) now rides the production registration, order-independent.
		ResultSerializers.Register(model);

		_ = _parityReportSurrogateRegistered.Value;

		var principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test"));

		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.ClearProviders();

		builder.Services.AddAuthorizationBuilder().AddPolicy(ParityPolicies.Public, policy => policy.RequireAssertion(_ => true));
		builder.Services.AddNorsePipeline();
		builder.Services.AddNorseCodeFirstGrpc();

		// Mirrors exactly what Asgard's registration generator emits for a real handled request --
		// ParityService/EchoParityCommand/EchoParityHandler/ParityRequestValidator are test-local (never
		// seen by any generator's own compilation), so the wiring is hand-registered here, same technique
		// MediatorParityTests.CreateHost uses for TestLoginCommand/StubLoginHandler.
		builder.Services.AddScoped<IRequestHandler<EchoParityCommand, ParityReport>, EchoParityHandler>();
		builder.Services.AddSingleton<ISenderDispatch, SenderDispatch<EchoParityCommand, ParityReport>>();
		builder.Services.AddScoped<IValidator<ParityRequest>, ParityRequestValidator>();
		builder.Services.AddScoped<IValidator<EchoParityCommand>, CommandRequestValidator<EchoParityCommand, ParityRequest, ParityReport>>();
		builder.Services.AddScoped<IParityService, ParityService>();
		builder.Services.AddScoped<IPrincipalAccessor>(_ => new SwoopPrincipalAccessor(principal));

		builder.Services
			.AddControllers()
			.AddNorseJson(Norse.Hosting.Web.Server.Tests.NorseXmlShapes.NorseEnumNameRegistration.Build())
			.AddNorseXml(XmlCaseStyle.CamelCase, Norse.Hosting.Web.Server.Tests.NorseXmlShapes.NorseXmlShapeRegistration.Build());
		builder.Services.AddOpenApi(options =>
		{
			options.AddSchemaTransformer<ResultSchemaTransformer>();
			options.AddSchemaTransformer<EnumSchemaTransformer>();
			options.AddSchemaTransformer<XmlMetadataTransformer>();
			options.AddDocumentTransformer<UnionLeakGuardTransformer>();
		});

		App = builder.Build();
		App.MapControllers();
		App.MapOpenApi();
		App.MapGrpcService<ParityService>();

		await App.StartAsync().ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync()
	{
		await App.StopAsync().ConfigureAwait(false);
		await App.DisposeAsync().ConfigureAwait(false);
	}

	/// <summary>
	/// The real typed client proxy — <c>ProtoBuf.Grpc.Client.GrpcClientFactory</c> over a plain
	/// unencrypted <see cref="TestServer"/> channel, mirrors <c>Hosting.Web.Client</c>'s own gRPC wiring
	/// idiom and <c>CountryLookupE2ETests</c>/<c>MediatorParityTests</c>' established test-host pattern.
	/// The mirror-contract era (a hand-built plain-field twin of <c>ParityRequest</c> serialized through
	/// the normal model, because <c>ResultSerializer&lt;T&gt;.Write</c> once threw unconditionally on
	/// every state) is over — Task 1's success-unwrap ruling
	/// (<c>../Glitnir/docs/Platform/specs/2026-08-02-result-success-unwrap-on-serialize-design.md</c>
	/// §2) restored a legal write for the <see cref="Success{T}"/> case on every channel, so a real
	/// <c>ParityRequest</c> built through the implicit <c>T → Result&lt;T&gt;</c> conversion now
	/// serializes for real. <see cref="EchoRawAsync"/> below is the one deliberate survivor: an omitting
	/// client (every field genuinely absent) can never be authored through a proxy that throws on
	/// default, so the honest fixture for that one case is still hand-built raw bytes, per the spec's
	/// named exception.
	/// </summary>
	public IParityService CreateClient()
	{
		var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = App.GetTestServer().CreateHandler() });
		var invoker = channel.Intercept(new OutcomeClientInterceptor());
		return GrpcClientFactory.CreateGrpcService<IParityService>(invoker);
	}

	/// <summary>
	/// Invokes <see cref="IParityService.EchoAsync"/> with hand-built wire bytes for the request,
	/// decoding <see cref="Outcome{T}"/> failures via <see cref="OutcomeClientInterceptor"/> exactly as
	/// <see cref="CreateClient"/>'s type-safe proxy would. The one surviving raw-bytes path (see
	/// <see cref="CreateClient"/>'s remarks): <paramref name="requestBytes"/> is caller-supplied wire
	/// bytes, used only for <c>Required_absent_detail_wording…</c>'s zero-byte "every field genuinely
	/// absent" case, which no client proxy can express by construction.
	/// </summary>
	public async Task<Outcome<ParityReport>> EchoRawAsync(byte[] requestBytes, CancellationToken cancellationToken)
	{
		var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = App.GetTestServer().CreateHandler() });
		var invoker = channel.Intercept(new OutcomeClientInterceptor());

		Method<byte[], Outcome<ParityReport>> method = new(
			MethodType.Unary,
			serviceName: "grpc.parity.v1.ParityService",
			// protobuf-net.Grpc strips the "Async" suffix from the C# method name by convention (no
			// [OperationContract(Name=...)] override on IParityService.EchoAsync) -- confirmed empirically
			// against the real bound path via a throwaway interceptor, never guessed: the type-safe
			// client proxy's own ClientInterceptorContext.Method.FullName reads
			// "/grpc.parity.v1.ParityService/Echo".
			name: "Echo",
			requestMarshaller: Marshallers.Create<byte[]>(static bytes => bytes, static bytes => bytes),
			responseMarshaller: Marshallers.Create<Outcome<ParityReport>>(
				serializer: static _ => throw new NotSupportedException($"{nameof(EchoRawAsync)} never serializes a response — this marshaller direction is client-inbound only."),
				deserializer: static bytes =>
				{
					using MemoryStream stream = new(bytes);
					return (Outcome<ParityReport>)RuntimeTypeModel.Default.Deserialize(stream, null, typeof(Outcome<ParityReport>))!;
				}));

		using var call = invoker.AsyncUnaryCall(method, host: null, new CallOptions(cancellationToken: cancellationToken), requestBytes);
		return await call.ResponseAsync;
	}
}

sealed class SwoopPrincipalAccessor(ClaimsPrincipal principal) : IPrincipalAccessor
{
	public ValueTask<ClaimsPrincipal> GetPrincipalAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(principal);
}

/// <summary>The test client's own ISO 8601 duration reader for <see cref="TimeSpan"/> -- see <see cref="TriProtocolSwoopTests.CreateFutharkTestJsonOptions"/>'s remarks.</summary>
sealed class IsoDurationTestJsonConverter : System.Text.Json.Serialization.JsonConverter<TimeSpan>
{
	public override TimeSpan Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
		System.Xml.XmlConvert.ToTimeSpan(reader.GetString()!);

	public override void Write(System.Text.Json.Utf8JsonWriter writer, TimeSpan value, System.Text.Json.JsonSerializerOptions options) =>
		writer.WriteStringValue(System.Xml.XmlConvert.ToString(value));
}

/// <summary>
/// The test client's own governed-name reader/writer for <see cref="ParityStatus"/> -- see
/// <see cref="TriProtocolSwoopTests.CreateFutharkTestJsonOptions"/>'s remarks. Plain STJ's built-in
/// enum handling reads/writes the CLR member name (<c>"Active"</c>), not the governed CamelCase wire
/// name (<c>"active"</c>) the server's <c>PlainEnumJsonConverter&lt;TEnum&gt;</c> produces, so a
/// Futhark-aware consumer configures its own name mapping, mirroring
/// <see cref="TriProtocolSwoopTests.ParseStatus"/>'s hand-rolled XML-side lookup.
/// </summary>
sealed class ParityStatusTestJsonConverter : System.Text.Json.Serialization.JsonConverter<ParityStatus>
{
	public override ParityStatus Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
		reader.GetString() switch
		{
			"active" => ParityStatus.Active,
			"inactive" => ParityStatus.Inactive,
			var other => throw new System.Text.Json.JsonException($"'{other}' is not a governed ParityStatus name.")
		};

	public override void Write(System.Text.Json.Utf8JsonWriter writer, ParityStatus value, System.Text.Json.JsonSerializerOptions options) =>
		writer.WriteStringValue(value == ParityStatus.Active ? "active" : "inactive");
}

[CollectionDefinition(SwoopCollection.Name)]
[SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "xUnit collection fixture naming convention")]
public sealed class SwoopCollection : ICollectionFixture<SwoopHostFixture>
{
	public const string Name = "Swoop";
}

/// <summary>
/// The tri-protocol swoop (spec §15) — one <see cref="ParityRequest"/>, driven through gRPC,
/// REST-JSON, and REST-XML against the live <see cref="SwoopHostFixture.App"/>: success parity
/// (structural equality of the resulting <see cref="ParityReport"/>), failure parity (identical
/// <c>errors</c> payload shape on the two text channels, literal-equal required-missing wording on the
/// gRPC leg), the required <c>Result&lt;string&gt;</c> = <c>""</c> round-trip spine, and the live-host
/// body cap.
/// </summary>
[Collection(SwoopCollection.Name)]
public sealed class TriProtocolSwoopTests(SwoopHostFixture fixture)
{
	// The one canonical, valid value set every channel's request body encodes -- §7's non-enum rows,
	// each formatted per its channel's own pinned lexical form (XmlLexical.Format / spec §7 table).
	static readonly Guid _identifier = Guid.Parse("0b917371-0000-0000-0000-000000000001");
	static readonly DateTime _timestamp = new(2026, 8, 1, 14, 30, 0, DateTimeKind.Utc);
	static readonly DateTimeOffset _timestampOffset = new(2026, 8, 1, 14, 30, 0, TimeSpan.FromHours(2));
	static readonly DateOnly _effectiveDate = new(2026, 8, 1);
	static readonly TimeOnly _startTime = new(14, 30, 0);
	static readonly TimeSpan _duration = new(1, 2, 3, 4);

	/// <summary>
	/// A valid <see cref="ParityRequest"/>, constructed directly through Task 0's implicit
	/// <c>T → Result&lt;T&gt;</c> conversion — the real type-safe shape <see cref="SwoopHostFixture.CreateClient"/>'s
	/// proxy can now legally serialize, mirror-contract era over (see that method's remarks).
	/// </summary>
	static ParityRequest ValidRequest(string name = "Alice") =>
		new()
		{
			IsActive = true,
			Count = 42,
			Amount = 1234.56m,
			Ratio = 1.5f,
			Measurement = 2.25d,
			Initial = 'A',
			Name = name,
			Identifier = _identifier,
			Timestamp = _timestamp,
			TimestampOffset = _timestampOffset,
			EffectiveDate = _effectiveDate,
			StartTime = _startTime,
			Duration = _duration,
			Status = ParityStatus.Active,
			Tags = [new() { Value = "tag-one" }, new() { Value = "tag-two" }]
		};

	const string JsonBody = """
		{
			"isActive": true,
			"count": 42,
			"amount": 1234.56,
			"ratio": 1.5,
			"measurement": 2.25,
			"initial": "A",
			"name": "Alice",
			"identifier": "0b917371-0000-0000-0000-000000000001",
			"timestamp": "2026-08-01T14:30:00.0000000Z",
			"timestampOffset": "2026-08-01T14:30:00.0000000+02:00",
			"effectiveDate": "2026-08-01",
			"startTime": "14:30:00.0000000",
			"duration": "P1DT2H3M4S",
			"status": "active",
			"tags": [ { "value": "tag-one" }, { "value": "tag-two" } ]
		}
		""";

	const string XmlBody = """
		<?xml version="1.0" encoding="utf-8"?>
		<parityRequest isActive="true" count="42" amount="1234.56" ratio="1.5" measurement="2.25" initial="A" name="Alice" identifier="0b917371-0000-0000-0000-000000000001" timestamp="2026-08-01T14:30:00.0000000Z" timestampOffset="2026-08-01T14:30:00.0000000+02:00" effectiveDate="2026-08-01" startTime="14:30:00.0000000" duration="P1DT2H3M4S" status="active">
			<parityTag value="tag-one" />
			<parityTag value="tag-two" />
		</parityRequest>
		""";

	static readonly System.Text.Json.JsonSerializerOptions _futharkTestJsonOptions = CreateFutharkTestJsonOptions();

	static System.Text.Json.JsonSerializerOptions CreateFutharkTestJsonOptions()
	{
		// Midgard's own DateTimeLexicalJsonConverter/DateTimeOffsetLexicalJsonConverter/
		// TimeOnlyLexicalJsonConverter/TimeSpanLexicalJsonConverter/PlainEnumJsonConverter<TEnum>
		// (Infrastructure.Web.Server.Json) are internal, no InternalsVisibleTo grant to this assembly --
		// and plain STJ already reads DateTime/DateTimeOffset/TimeOnly's "O"-format text natively
		// (matches spec §7's pinned form byte-for-byte), so only TimeSpan (ISO 8601 duration, not STJ's
		// default "c" format) and ParityStatus (STJ's default enum converter reads/writes the CLR member
		// name, not the governed CamelCase wire name) need stand-in converters here, matching how a real
		// Futhark-aware JSON consumer would configure its own.
		System.Text.Json.JsonSerializerOptions options = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
		options.Converters.Add(new IsoDurationTestJsonConverter());
		options.Converters.Add(new ParityStatusTestJsonConverter());
		return options;
	}

	static void AssertCanonicalReport(ParityReport report)
	{
		report.IsActive.ShouldBeTrue();
		report.Count.ShouldBe(42);
		report.Amount.ShouldBe(1234.56m);
		report.Ratio.ShouldBe(1.5f);
		report.Measurement.ShouldBe(2.25d);
		report.Initial.ShouldBe('A');
		report.Name.ShouldBe("Alice");
		report.Identifier.ShouldBe(_identifier);
		report.Timestamp.ShouldBe(_timestamp);
		report.TimestampOffset.ShouldBe(_timestampOffset);
		report.EffectiveDate.ShouldBe(_effectiveDate);
		report.StartTime.ShouldBe(_startTime);
		report.Duration.ShouldBe(_duration);
		report.Status.ShouldBe(ParityStatus.Active);
		report.Tags.Select(t => t.Value).ShouldBe(["tag-one", "tag-two"]);
	}

	[Fact]
	async Task Success_parity_the_same_request_renders_a_structurally_equal_report_on_all_three_channels()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

		var grpcOutcome = await fixture.CreateClient().EchoAsync(ValidRequest(), cancellationToken);
		grpcOutcome.TryGetValue(out Success<ParityReport> grpcSuccess).ShouldBeTrue();
		var grpcReport = grpcSuccess.Value;

		using var jsonClient = fixture.App.GetTestClient();
		using var jsonRequest = new HttpRequestMessage(HttpMethod.Post, "/api/parity") { Content = new StringContent(JsonBody, Encoding.UTF8, "application/json") };
		jsonRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		using var jsonResponse = await jsonClient.SendAsync(jsonRequest, cancellationToken);
		jsonResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
		// A plain System.Text.Json.JsonSerializerOptions has no idea about Futhark's pinned lexical
		// forms (ISO 8601 duration for TimeSpan et al., spec §7) -- the server writes them correctly via
		// AddNorseJson's converters, so the test client needs the same converters to read them back, the
		// same way a real Futhark-aware consumer would configure its own JSON client.
		var jsonReport = await jsonResponse.Content.ReadFromJsonAsync<ParityReport>(_futharkTestJsonOptions, cancellationToken);

		using var xmlClient = fixture.App.GetTestClient();
		using var xmlRequest = new HttpRequestMessage(HttpMethod.Post, "/api/parity") { Content = new StringContent(XmlBody, Encoding.UTF8, "application/xml") };
		xmlRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
		using var xmlResponse = await xmlClient.SendAsync(xmlRequest, cancellationToken);
		xmlResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
		var xmlReportText = await xmlResponse.Content.ReadAsStringAsync(cancellationToken);
		var xmlReport = ParseParityReportXml(xmlReportText);

		AssertCanonicalReport(grpcReport);
		AssertCanonicalReport(jsonReport!);
		AssertCanonicalReport(xmlReport);

		// Structural equality across channels, not merely "didn't throw" -- every field on every
		// channel's report must agree with every other channel's, not just with the canonical constants.
		jsonReport.ShouldBe(grpcReport with { Tags = jsonReport!.Tags });
		xmlReport.ShouldBe(grpcReport with { Tags = xmlReport.Tags });
	}

	/// <summary>Hand-rolled: <see cref="ParityReport"/> carries no generated <em>reader</em> shape in this suite's own assertions -- only the host's registered <c>XmlContractOutputFormatter</c> needs one, which it has.</summary>
	static ParityReport ParseParityReportXml(string xml)
	{
		var doc = System.Xml.Linq.XDocument.Parse(xml);
		var root = doc.Root!;
		return new ParityReport
		{
			IsActive = bool.Parse(root.Attribute("isActive")!.Value),
			Count = int.Parse(root.Attribute("count")!.Value, System.Globalization.CultureInfo.InvariantCulture),
			Amount = decimal.Parse(root.Attribute("amount")!.Value, System.Globalization.CultureInfo.InvariantCulture),
			Ratio = float.Parse(root.Attribute("ratio")!.Value, System.Globalization.CultureInfo.InvariantCulture),
			Measurement = double.Parse(root.Attribute("measurement")!.Value, System.Globalization.CultureInfo.InvariantCulture),
			Initial = root.Attribute("initial")!.Value[0],
			Name = root.Attribute("name")!.Value,
			Identifier = Guid.Parse(root.Attribute("identifier")!.Value),
			Timestamp = DateTime.Parse(root.Attribute("timestamp")!.Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
			TimestampOffset = DateTimeOffset.Parse(root.Attribute("timestampOffset")!.Value, System.Globalization.CultureInfo.InvariantCulture),
			EffectiveDate = DateOnly.Parse(root.Attribute("effectiveDate")!.Value, System.Globalization.CultureInfo.InvariantCulture),
			StartTime = TimeOnly.Parse(root.Attribute("startTime")!.Value, System.Globalization.CultureInfo.InvariantCulture),
			Duration = System.Xml.XmlConvert.ToTimeSpan(root.Attribute("duration")!.Value),
			Status = ParseStatus(root.Attribute("status")!.Value),
			Tags = [.. root.Elements().Select(e => new ParityReportTag { Value = e.Attribute("value")!.Value })]
		};
	}

	/// <summary>Hand-rolled governed-name lookup, mirroring the generated shapes' own table -- CamelCase style, §7.4.</summary>
	static ParityStatus ParseStatus(string text) => text switch
	{
		"active" => ParityStatus.Active,
		"inactive" => ParityStatus.Inactive,
		_ => throw new FormatException($"'{text}' is not a governed ParityStatus name.")
	};

	[Fact]
	async Task Failure_parity_three_malformed_scalars_render_identical_errors_arrays_on_json_and_xml()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

		const string MalformedJson = """
			{
				"isActive": true, "count": 42, "amount": 1234.56, "ratio": 1.5, "measurement": 2.25,
				"initial": "A", "name": "Alice", "identifier": "0b917371-0000-0000-0000-000000000001",
				"timestamp": "2026-08-01T14:30:00.0000000Z", "timestampOffset": "2026-08-01T14:30:00.0000000+02:00",
				"effectiveDate": "not-a-date", "startTime": "not-a-time", "duration": "not-a-duration",
				"status": "active"
			}
			""";
		const string MalformedXml = """
			<?xml version="1.0" encoding="utf-8"?>
			<parityRequest isActive="true" count="42" amount="1234.56" ratio="1.5" measurement="2.25" initial="A" name="Alice" identifier="0b917371-0000-0000-0000-000000000001" timestamp="2026-08-01T14:30:00.0000000Z" timestampOffset="2026-08-01T14:30:00.0000000+02:00" effectiveDate="not-a-date" startTime="not-a-time" duration="not-a-duration" status="active" />
			""";

		var jsonErrors = await PostAndReadProblemErrorsAsync(MalformedJson, "application/json", cancellationToken);
		var xmlErrors = await PostAndReadProblemErrorsAsync(MalformedXml, "application/xml", cancellationToken);

		jsonErrors.Count.ShouldBe(3);
		jsonErrors.OrderBy(e => e.Path, StringComparer.Ordinal).ShouldBe(xmlErrors.OrderBy(e => e.Path, StringComparer.Ordinal));
	}

	async Task<List<(string Path, string Detail)>> PostAndReadProblemErrorsAsync(string body, string mediaType, CancellationToken cancellationToken)
	{
		using var client = fixture.App.GetTestClient();
		using var request = new HttpRequestMessage(HttpMethod.Post, "/api/parity") { Content = new StringContent(body, Encoding.UTF8, mediaType) };
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
		using var response = await client.SendAsync(request, cancellationToken);
		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		var text = await response.Content.ReadAsStringAsync(cancellationToken);
		List<(string Path, string Detail)> errors;
		if (mediaType == "application/json")
		{
			var problem = System.Text.Json.Nodes.JsonNode.Parse(text)!;
			errors = [.. problem["errors"]!.AsArray().Select(e => (e!["path"]!.GetValue<string>(), e["detail"]!.GetValue<string>()))];
		}
		else
		{
			var doc = System.Xml.Linq.XDocument.Parse(text);
			errors = [.. doc.Root!.Elements().Where(e => e.Name.LocalName == "errors").SelectMany(e => e.Elements())
				.Select(item => (item.Elements().First(x => x.Name.LocalName == "path").Value, item.Elements().First(x => x.Name.LocalName == "detail").Value))];
		}

		// The Task 13 finding that used to force a filter here -- [ApiController]'s implicit
		// non-nullable-reference-type [Required] double-firing as an extra "request" ModelState entry
		// whenever the XML input formatter returned Failure -- is fixed at the law level:
		// AddNorseXml sets SuppressImplicitRequiredAttributeForNonNullableReferenceTypes, since
		// required-ness on Futhark contracts is carried by Result<T> presence semantics plus
		// ResultRules validation, never MVC's DataAnnotations layer. Unfiltered on purpose: if the
		// artifact ever comes back, the failure-parity assertions below go loudly asymmetric again.
		return errors;
	}

	[Fact]
	async Task Required_absent_detail_wording_is_literally_equal_across_all_three_channels()
	{
		// spec §9.3/§8.2, the Task 4 FailureDetail.Render-parity condition, now provable end-to-end on
		// gRPC too: an empty wire payload (zero bytes) means every field's tag is genuinely absent --
		// mirrors ResultSerializerTests.An_absent_field_deserializes_to_a_default_Result's own proof that
		// this decodes every Result<T> member to default(Result<T>), the identical semantic state an
		// omitted JSON property or a missing XML attribute produces. Never touches ResultSerializer<T>.
		// Write (which throws unconditionally now, Midgard commit 08e1357/1e31d9a/f175275) -- the payload
		// is simply zero bytes, no serialization of anything Result-shaped involved at all.
		var cancellationToken = TestContext.Current.CancellationToken;

		var grpcOutcome = await fixture.EchoRawAsync([], cancellationToken);
		grpcOutcome.TryGetValue(out Failed grpcFailed).ShouldBeTrue();
		grpcFailed.Problem.Category.ShouldBe(ErrorCategory.Validation);
		grpcFailed.Problem.Errors.Values.SelectMany(v => v).ShouldAllBe(detail => detail == "required value missing");

		var xmlErrors = await PostAndReadProblemErrorsAsync(
			"""<?xml version="1.0" encoding="utf-8"?><parityRequest name="" />""", "application/xml", cancellationToken);
		xmlErrors.ShouldContain(e => e.Detail == "required value missing");
		xmlErrors.Count.ShouldBeGreaterThan(1); // every other required attribute is likewise absent

		var jsonErrors = await PostAndReadProblemErrorsAsync("{}", "application/json", cancellationToken);
		jsonErrors.ShouldContain(e => e.Detail == "required value missing");
		jsonErrors.Select(e => e.Detail).Distinct().ShouldBe(["required value missing"]);

		// FailureDetail.Render parity, literally: the same message text, independent of which channel
		// or which mechanism (formatter-level ModelState vs. mediator ValidationBehavior) produced it.
		grpcFailed.Problem.Errors.Values.SelectMany(v => v).Distinct().ShouldBe(["required value missing"]);
	}

	[Fact]
	async Task Required_result_string_carrying_empty_content_round_trips_and_succeeds()
	{
		// Spec §8.2: "" is present-legitimately-empty content for a required Result<string> -- distinct
		// from absence -- and must succeed, never route through the "required missing" funnel. Proven
		// here via the real typed client (Name field present, explicitly empty) -- ResultSerializerTests.
		// Round_trips_Result_of_an_empty_string proves protobuf-net itself writes a present-but-empty
		// string field distinctly from an absent one, the same presence/emptiness law Futhark's own
		// generated shapes enforce on the text channels.
		var cancellationToken = TestContext.Current.CancellationToken;

		var outcome = await fixture.CreateClient().EchoAsync(ValidRequest(name: ""), cancellationToken);

		outcome.TryGetValue(out Success<ParityReport> success).ShouldBeTrue();
		success.Value.Name.ShouldBe("");
	}

	[Fact]
	async Task Opt_in_law_the_undecorated_binding_shadow_is_invisible_on_every_channel()
	{
		// spec §4b: StatusText is undecorated, so under the opt-in law it does not exist to STJ's
		// membership definition or the XML closure walker -- naming it inbound is the same "member not
		// on the contract" violation JsonUnmappedMemberHandling.Disallow already rejects for any
		// stranger key (Futhark spec §8.1's strictness-parity ratchet), and it can never appear
		// outbound because no writer walk (protobuf-net, STJ, the generated XML writer) ever discovers
		// an undecorated member in the first place.
		var cancellationToken = TestContext.Current.CancellationToken;

		const string BodyNamingTheShadow = """
			{
				"isActive": true, "count": 42, "amount": 1234.56, "ratio": 1.5, "measurement": 2.25,
				"initial": "A", "name": "Alice", "identifier": "0b917371-0000-0000-0000-000000000001",
				"timestamp": "2026-08-01T14:30:00.0000000Z", "timestampOffset": "2026-08-01T14:30:00.0000000+02:00",
				"effectiveDate": "2026-08-01", "startTime": "14:30:00.0000000", "duration": "P1DT2H3M4S",
				"status": "active", "statusText": "Active"
			}
			""";

		using var jsonClient = fixture.App.GetTestClient();
		using var jsonRequest = new HttpRequestMessage(HttpMethod.Post, "/api/parity") { Content = new StringContent(BodyNamingTheShadow, Encoding.UTF8, "application/json") };
		jsonRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		using var jsonResponse = await jsonClient.SendAsync(jsonRequest, cancellationToken);
		jsonResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		using var xmlClient = fixture.App.GetTestClient();
		using var xmlRequest = new HttpRequestMessage(HttpMethod.Post, "/api/parity") { Content = new StringContent(XmlBody, Encoding.UTF8, "application/xml") };
		xmlRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
		using var xmlResponse = await xmlClient.SendAsync(xmlRequest, cancellationToken);
		xmlResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
		var xmlText = await xmlResponse.Content.ReadAsStringAsync(cancellationToken);
		xmlText.ShouldNotContain("statusText");
	}

	[Fact]
	async Task Body_cap_an_oversized_xml_body_is_rejected_with_413_by_the_live_host()
	{
		// GrpcControllerBase carries [RequestSizeLimit(1_048_576)] (spec §8.4) -- Kestrel's own
		// IHttpMaxRequestBodySizeFeature is what actually enforces it, and Microsoft.AspNetCore.TestHost's
		// in-memory transport does not implement that enforcement (a documented TestServer limitation,
		// empirically confirmed here: the oversized POST below reaches the controller and formatter
		// unrejected against fixture.App). This test therefore boots a REAL Kestrel listener on a loopback
		// port instead of TestServer -- still "the live host" per the brief, just the one live-host
		// flavor that can actually prove a Kestrel-level cap.
		var cancellationToken = TestContext.Current.CancellationToken;

		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Logging.ClearProviders();
		builder.Services.AddAuthorizationBuilder().AddPolicy(ParityPolicies.Public, policy => policy.RequireAssertion(_ => true));
		builder.Services.AddNorsePipeline();
		builder.Services.AddScoped<IRequestHandler<EchoParityCommand, ParityReport>, EchoParityHandler>();
		builder.Services.AddSingleton<ISenderDispatch, SenderDispatch<EchoParityCommand, ParityReport>>();
		builder.Services.AddScoped<IValidator<ParityRequest>, ParityRequestValidator>();
		builder.Services.AddScoped<IValidator<EchoParityCommand>, CommandRequestValidator<EchoParityCommand, ParityRequest, ParityReport>>();
		builder.Services.AddScoped<IParityService, ParityService>();
		builder.Services.AddScoped<IPrincipalAccessor>(_ => new SwoopPrincipalAccessor(new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test"))));
		builder.Services.AddControllers()
			.AddNorseJson(Norse.Hosting.Web.Server.Tests.NorseXmlShapes.NorseEnumNameRegistration.Build())
			.AddNorseXml(XmlCaseStyle.CamelCase, Norse.Hosting.Web.Server.Tests.NorseXmlShapes.NorseXmlShapeRegistration.Build());

		await using var app = builder.Build();
		app.MapControllers();
		await app.StartAsync(cancellationToken);

		var padding = new string('a', 1_100_000);
		var oversized = $"""<?xml version="1.0" encoding="utf-8"?><parityRequest isActive="true" count="1" amount="1" ratio="1" measurement="1" initial="A" name="{padding}" identifier="0b917371-0000-0000-0000-000000000001" timestamp="2026-08-01T00:00:00.0000000Z" timestampOffset="2026-08-01T00:00:00.0000000+00:00" effectiveDate="2026-08-01" startTime="00:00:00.0000000" duration="PT1S" status="active" />""";

		using HttpClient client = new() { BaseAddress = new Uri(app.Urls.First()) };
		using var request = new HttpRequestMessage(HttpMethod.Post, "/api/parity") { Content = new StringContent(oversized, Encoding.UTF8, "application/xml") };
		using var response = await client.SendAsync(request, cancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);

		await app.StopAsync(cancellationToken);
	}
}
