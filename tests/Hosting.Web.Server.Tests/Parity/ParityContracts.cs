using System.Runtime.Serialization;
using Norse.Primitives;

namespace Norse.Hosting.Web.Server.Tests.Parity;

/// <summary>
/// The tri-protocol swoop's own request contract (Task 13) — <see cref="Result{T}"/>-wrapped members
/// covering every non-enum row of the Futhark spec §7 lexical table, plus a collection of
/// <see cref="ParityTag"/> (Futhark's "collection items are complex types only" law, §5.8). The enum
/// row (<see cref="ParityStatus"/>) is deliberately absent from this request closure — see the remark
/// on <see cref="ParityReport.Status"/> for why: it is a real, confirmed platform gap this task found,
/// not an oversight here.
/// </summary>
[DataContract]
public sealed record ParityRequest
{
	[DataMember(Order = 1)] public Result<bool> IsActive { get; init; }
	[DataMember(Order = 2)] public Result<int> Count { get; init; }
	[DataMember(Order = 3)] public Result<decimal> Amount { get; init; }
	[DataMember(Order = 4)] public Result<float> Ratio { get; init; }
	[DataMember(Order = 5)] public Result<double> Measurement { get; init; }
	[DataMember(Order = 6)] public Result<char> Initial { get; init; }
	[DataMember(Order = 7)] public Result<string> Name { get; init; }
	[DataMember(Order = 8)] public Result<Guid> Identifier { get; init; }
	[DataMember(Order = 9)] public Result<DateTime> Timestamp { get; init; }
	[DataMember(Order = 10)] public Result<DateTimeOffset> TimestampOffset { get; init; }
	[DataMember(Order = 11)] public Result<DateOnly> EffectiveDate { get; init; }
	[DataMember(Order = 12)] public Result<TimeOnly> StartTime { get; init; }
	[DataMember(Order = 13)] public Result<TimeSpan> Duration { get; init; }
	[DataMember(Order = 14)] public List<ParityTag> Tags { get; init; } = [];
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
	/// §7's enum row — carried here, response-side only, raw (never <c>Result&lt;ParityStatus&gt;</c>).
	/// <b>Cross-task finding (Task 13):</b> Midgard's <c>Infrastructure.Web.Grpc/ResultSerializers.cs</c>
	/// registers a protobuf-net surrogate for <c>Result&lt;T&gt;</c> over exactly the BCL half of §7's
	/// taxonomy (<c>RegisterScalar&lt;T&gt;</c> is constrained <c>where T : notnull, ISpanParsable&lt;T&gt;</c>,
	/// and <c>ResultSerializer&lt;T&gt;</c>'s own <c>ReadScalar</c>/<c>WriteScalar</c> are closed
	/// <c>typeof</c> branches over the same 13 BCL types) — no branch exists for an enum, and a plain
	/// enum cannot satisfy <c>ISpanParsable&lt;T&gt;</c>, so <c>Result&lt;TEnum&gt;</c> has no gRPC wire
	/// law at all today, even though spec §5.3/§7 list enums as a full member of the same closed scalar
	/// taxonomy every other row of this file wraps in <c>Result&lt;T&gt;</c> on the request side. Putting
	/// <c>Result&lt;ParityStatus&gt;</c> on <see cref="ParityRequest"/> would make this swoop's gRPC leg
	/// fail on a gap outside this task's remit to fix (Midgard's <c>ResultSerializers</c>/
	/// <c>ResultSerializer&lt;T&gt;</c>) — so the enum row is proven end-to-end here, response-side,
	/// where it needs no <c>Result&lt;T&gt;</c> surrogate at all (protobuf-net serializes a bare enum
	/// natively), and via the XML/JSON generated shapes' own enum name-table (spec §6.5), independent of
	/// protobuf. See the Task 13 report for the full writeup.
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
