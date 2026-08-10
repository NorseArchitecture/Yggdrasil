using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Norse.Abstractions.Contracts;
using Norse.Abstractions.Web.Server.Mediator;
using Norse.Infrastructure.Web.Server.Validation;

namespace Norse.Hosting.Web.Server.Tests.Parity;

/// <summary>
///     The permissive policy every mediator request in this swoop fixture carries — mirrors
///     <c>AuthNPolicies.Public</c>/<c>ReferencePolicies.Public</c>.
/// </summary>
static class ParityPolicies
{
	public const string Public = "Parity.Public";
}

/// <summary>
///     The mediator identity <see cref="ParityService.EchoAsync" /> hydrates <see cref="ParityRequest" />
///     into and sends — the swoop's own mirror of Himinbjörg's real <c>LoginCommand</c>. Wire paths in the
///     <c>.OverridePropertyName(...)</c> calls below (<see cref="ParityRequestValidator" />) must match this command's
///     wrapped request exactly, since <c>ValidationBehavior</c> groups failures by
///     <c>ValidationFailure.PropertyName</c> — the only way JSON's failure paths land on the same
///     <c>{root}/@{attribute}</c> grammar XML's formatter produces natively (spec §11.2).
/// </summary>
[Authorize(Policy = ParityPolicies.Public)]
sealed record EchoParityCommand(ParityRequest Request) : CommandRequest<ParityRequest, ParityReport>(Request);

/// <summary>
///     Validates every <see cref="ParityRequest" /> scalar via <see cref="ResultRules" /> — the only place a
///     malformed/missing scalar on the JSON channel ever becomes a 400 (Task 13 research finding: unlike
///     XML, STJ's <c>Result&lt;T&gt;</c> converter never throws and never touches <c>ModelState</c>; a
///     captured <c>Failure</c> rides silently into the handler as ordinary data absent this validator).
///     <c>.OverridePropertyName(...)</c> on every rule pins <c>ValidationFailure.PropertyName</c> to the exact wire
///     path <c>AddNorseXml(XmlCaseStyle.CamelCase, ...)</c> would have produced for the same failure, so
///     JSON and XML render byte-identical <c>errors</c> paths — proven, not assumed, by
///     <c>TriProtocolSwoopTests</c>.
/// </summary>
sealed class ParityRequestValidator : AbstractValidator<ParityRequest>
{
	public ParityRequestValidator()
	{
		RuleFor(x => x.IsActive).ResultRequired().OverridePropertyName("parityRequest/@isActive");
		RuleFor(x => x.Count).ResultRequired().OverridePropertyName("parityRequest/@count");
		RuleFor(x => x.Amount).ResultRequired().OverridePropertyName("parityRequest/@amount");
		RuleFor(x => x.Ratio).ResultRequired().OverridePropertyName("parityRequest/@ratio");
		RuleFor(x => x.Measurement).ResultRequired().OverridePropertyName("parityRequest/@measurement");
		RuleFor(x => x.Initial).ResultRequired().OverridePropertyName("parityRequest/@initial");
		RuleFor(x => x.Name).ResultRequired().OverridePropertyName("parityRequest/@name");
		RuleFor(x => x.Identifier).ResultRequired().OverridePropertyName("parityRequest/@identifier");
		RuleFor(x => x.Timestamp).ResultRequired().OverridePropertyName("parityRequest/@timestamp");
		RuleFor(x => x.TimestampOffset).ResultRequired().OverridePropertyName("parityRequest/@timestampOffset");
		RuleFor(x => x.EffectiveDate).ResultRequired().OverridePropertyName("parityRequest/@effectiveDate");
		RuleFor(x => x.StartTime).ResultRequired().OverridePropertyName("parityRequest/@startTime");
		RuleFor(x => x.Duration).ResultRequired().OverridePropertyName("parityRequest/@duration");
		RuleFor(x => x.Status).ResultRequiredEnum().OverridePropertyName("parityRequest/@status");
	}
}

/// <summary>
///     Turns every accumulated <see cref="ParityRequest" /> scalar into a <see cref="ParityReport" /> field —
///     only ever reached once <see cref="ParityRequestValidator" /> has already proven every member
///     <see cref="Norse.Primitives.Success{T}" />, so the <c>Match</c> failure branch below is a genuine
///     "this should be unreachable" guard, not a real code path.
/// </summary>
sealed class EchoParityHandler : IRequestHandler<EchoParityCommand, ParityReport>
{
	public ValueTask<Outcome<ParityReport>> Handle(EchoParityCommand request,
		CancellationToken cancellationToken = default)
	{
		var wire = request.Request;
		ParityReport report = new()
		{
			IsActive = Unwrap(wire.IsActive),
			Count = Unwrap(wire.Count),
			Amount = Unwrap(wire.Amount),
			Ratio = Unwrap(wire.Ratio),
			Measurement = Unwrap(wire.Measurement),
			Initial = Unwrap(wire.Initial),
			Name = Unwrap(wire.Name),
			Identifier = Unwrap(wire.Identifier),
			Timestamp = Unwrap(wire.Timestamp),
			TimestampOffset = Unwrap(wire.TimestampOffset),
			EffectiveDate = Unwrap(wire.EffectiveDate),
			StartTime = Unwrap(wire.StartTime),
			Duration = Unwrap(wire.Duration),
			Status = Unwrap(wire.Status),
			Tags = [.. wire.Tags.Select(tag => new ParityReportTag { Value = Unwrap(tag.Value) })]
		};
		return ValueTask.FromResult(Outcome<ParityReport>.Ok(report));
	}

	static T Unwrap<T>(Norse.Primitives.Result<T> result) where T : notnull =>
		result.Match(static value => value, static failure => throw new InvalidOperationException(
			$"{nameof(EchoParityHandler)} reached a failed Result<{typeof(T).Name}> ({failure.Reason}) after ParityRequestValidator should have rejected it."));
}

/// <summary>
///     The tri-protocol swoop's gRPC-service-shaped implementation — same hydrate-and-send shape as
///     Himinbjörg's real <c>AuthenticationService.Login</c>: wraps the wire request in
///     <see cref="EchoParityCommand" /> and runs it through the real <see cref="ISender" /> pipeline
///     (telemetry, exception translation, authorization, validation), never a stub.
/// </summary>
sealed class ParityService(ISender sender) : IParityService
{
	[Authorize(Policy = ParityPolicies.Public)]
	public ValueTask<Outcome<ParityReport>> EchoAsync(ParityRequest request,
		CancellationToken cancellationToken = default) =>
		sender.Send(new EchoParityCommand(request), cancellationToken);
}
