using System.Runtime.Serialization;
using Norse.Primitives;

namespace Norse.Hosting.Web.Server.Tests.Parity;

/// <summary>
/// The tri-protocol swoop's own request contract (Task 13) — <see cref="Result{T}"/>-wrapped members
/// covering every row of the Futhark spec §7 lexical table, including the enum row
/// (<see cref="ParityStatus"/>, spec §7.4/§7's twentieth taxonomy row — live end to end on all three
/// channels as of Task 11; see the remark on <see cref="ParityReport.Status"/> for the response-side
/// half), plus a collection of <see cref="ParityTag"/> (Futhark's "collection items are complex types
/// only" law, §5.8). Deliberately mutable (<c>get; set;</c>, not <c>init</c>) on every member — the
/// "<c>LoginRequest</c> deliberately mutable" exception (Heimdall's <c>AuthN.Services.LoginRequest</c>)
/// is the standing rule for any request contract a form binds
/// (<c>../Glitnir/docs/Platform/specs/2026-08-02-result-success-unwrap-on-serialize-design.md</c> §4a
/// "Mutability consequence, ratified"): <see cref="StatusText"/>'s setter assigns <see cref="Status"/>
/// from ordinary instance code, which an <c>init</c> accessor cannot legally target outside a
/// constructor or object initializer.
/// </summary>
[DataContract]
public sealed record ParityRequest
{
	[DataMember(Order = 1)] public Result<bool> IsActive { get; set; }
	[DataMember(Order = 2)] public Result<int> Count { get; set; }
	[DataMember(Order = 3)] public Result<decimal> Amount { get; set; }
	[DataMember(Order = 4)] public Result<float> Ratio { get; set; }
	[DataMember(Order = 5)] public Result<double> Measurement { get; set; }
	[DataMember(Order = 6)] public Result<char> Initial { get; set; }
	[DataMember(Order = 7)] public Result<string> Name { get; set; }
	[DataMember(Order = 8)] public Result<Guid> Identifier { get; set; }
	[DataMember(Order = 9)] public Result<DateTime> Timestamp { get; set; }
	[DataMember(Order = 10)] public Result<DateTimeOffset> TimestampOffset { get; set; }
	[DataMember(Order = 11)] public Result<DateOnly> EffectiveDate { get; set; }
	[DataMember(Order = 12)] public Result<TimeOnly> StartTime { get; set; }
	[DataMember(Order = 13)] public Result<TimeSpan> Duration { get; set; }
	[DataMember(Order = 14)] public List<ParityTag> Tags { get; set; } = [];

	[DataMember(Order = 15)] public Result<ParityStatus> Status { get; set; }

	/// <summary>
	/// The §4a binding shadow, proven end-to-end: undecorated, so under the opt-in law it does not
	/// exist to protobuf-net, STJ, or the XML closure walker — no NORSE022, no wire presence, no
	/// second door. get derives from the union (Failure round-trips its Input); set runs a test-local
	/// <c>nameof</c>-comparison stand-in for the real parse funnel — the production pattern lives in
	/// <c>EnumLexical</c> over the generated per-enum name tables, never a form binder's own name
	/// comparison; this shadow exists only to prove the union round-trips through a form-bound property,
	/// the same way <c>ParityStatusTestJsonConverter</c> (Swoop) stands in for the server's governed
	/// JSON converter rather than being it.
	/// </summary>
	public string StatusText
	{
		get => Status.TryGetValue(out Success<ParityStatus> success) ?
			success.Value.ToString() :
			Status.TryGetValue(out Failure failure) ?
				failure.Input :
				"";
		set => Status = value == nameof(ParityStatus.Active) ?
			new Success<ParityStatus>(ParityStatus.Active) :
			value == nameof(ParityStatus.Inactive) ?
				new Success<ParityStatus>(ParityStatus.Inactive) :
				new Failure(ParseFailure.Malformed, value, nameof(ParityStatus));
	}
}

/// <summary>The role-named wrapper Futhark's "scalar collections have no shape" law (§5.8) requires for a collection of strings.</summary>
[DataContract]
public sealed record ParityTag
{
	[DataMember(Order = 1)] public Result<string> Value { get; init; }
}

/// <summary>
/// The tri-protocol swoop's response contract — plain (never <see cref="Result{T}"/>-wrapped, per
/// spec §5.4 "response scalars never wrap") scalars echoing every <see cref="ParityRequest"/> value.
/// A distinct type from <see cref="ParityRequest"/>/<see cref="ParityTag"/> throughout — spec §5.5
/// ("no type serves both masters") forbids a complex type reachable from both closures.
/// </summary>
[DataContract]
public sealed record ParityReport
{
	[DataMember(Order = 1)] public bool IsActive { get; init; }
	[DataMember(Order = 2)] public int Count { get; init; }
	[DataMember(Order = 3)] public decimal Amount { get; init; }
	[DataMember(Order = 4)] public float Ratio { get; init; }
	[DataMember(Order = 5)] public double Measurement { get; init; }
	[DataMember(Order = 6)] public char Initial { get; init; }
	[DataMember(Order = 7)] public string Name { get; init; } = "";
	[DataMember(Order = 8)] public Guid Identifier { get; init; }
	[DataMember(Order = 9)] public DateTime Timestamp { get; init; }
	[DataMember(Order = 10)] public DateTimeOffset TimestampOffset { get; init; }
	[DataMember(Order = 11)] public DateOnly EffectiveDate { get; init; }
	[DataMember(Order = 12)] public TimeOnly StartTime { get; init; }
	[DataMember(Order = 13)] public TimeSpan Duration { get; init; }

	/// <summary>
	/// §7's enum row (spec §7.4/§7's twentieth taxonomy row) — carried here, response-side, raw (never
	/// <c>Result&lt;ParityStatus&gt;</c>), because response scalars never wrap (spec §5.4). The
	/// request-side half of the same row lives on <see cref="ParityRequest.Status"/>, <c>Result</c>-
	/// wrapped, and is live on every channel now, not merely designed: gRPC via Midgard's
	/// <c>ResultEnumSerializer&lt;TEnum&gt;</c> (discovery-registered varint wire law, undefined values
	/// funnel to the platform's typed <c>Failure</c>), JSON via <c>ResultEnumJsonConverterFactory</c>/
	/// <c>ResultEnumJsonConverter&lt;TEnum&gt;</c> over the generated <c>EnumNameRegistry</c>, and XML
	/// via the same generated shapes' own enum name-table — three consumers of one governed table, per
	/// <c>../Glitnir/docs/Platform/specs/2026-08-02-futhark-enum-wire-law-design.md</c>. This raw member
	/// stays governed the same way: <c>EnumLexical</c> over the identical table, never the CLR ordinal.
	/// <see cref="ParityRequest"/>'s mutability (<c>get; set;</c>, not <c>init</c>) and its
	/// <c>StatusText</c> on-contract binding shadow are ratified by
	/// <c>../Glitnir/docs/Platform/specs/2026-08-02-result-success-unwrap-on-serialize-design.md</c>
	/// §4a/§4b.
	/// </summary>
	[DataMember(Order = 14)] public ParityStatus Status { get; init; }

	[DataMember(Order = 15)] public List<ParityReportTag> Tags { get; init; } = [];
}

/// <summary>The response-side counterpart to <see cref="ParityTag"/> — a distinct type, raw (non-<c>Result</c>) <see cref="Value"/>.</summary>
[DataContract]
public sealed record ParityReportTag
{
	[DataMember(Order = 1)] public string Value { get; init; } = "";
}

/// <summary>§7's enum row, spelled out with explicit values per platform enum convention.</summary>
public enum ParityStatus
{
	Active = 1,
	Inactive = 2
}
