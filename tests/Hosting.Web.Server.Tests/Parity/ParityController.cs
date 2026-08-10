using Microsoft.AspNetCore.Mvc;
using Norse.Abstractions.Web.Server.Facade;

namespace Norse.Hosting.Web.Server.Tests.Parity;

/// <summary>
///     The tri-protocol swoop's REST facade (spec §4) — injects <see cref="IParityService" /> directly and
///     runs it in-process, no protobuf on the path, the same mediator pipeline underneath as the gRPC leg
///     (<c>Swoop.SwoopHostFixture</c> maps both onto this one <see cref="ParityService" /> instance). This
///     is the sole <see cref="GrpcControllerBase" /> descendant in this compilation, so the Xml shape
///     generator's closure walk (spec §4.1) sees exactly one request closure (<see cref="ParityRequest" />)
///     and one response closure (<see cref="ParityReport" />) — the "generator emitted shapes for exactly
///     the parity contracts" self-review condition (Task 13 brief, Step 4).
/// </summary>
[Route("api/parity")]
public sealed class ParityController(IParityService parityService) : GrpcControllerBase
{
	[HttpPost]
	public Task<ActionResult<ParityReport>> Echo([FromBody] ParityRequest request) =>
		FoldAsync(parityService.EchoAsync(request));
}
