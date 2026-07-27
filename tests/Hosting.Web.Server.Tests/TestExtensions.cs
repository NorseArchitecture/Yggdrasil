using Grpc.Core;
using Norse.Abstractions.Contracts;

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
/// Thin aliases over Midgard's real <see cref="Norse.Infrastructure.Web.Server.Mediator.Grpc.ProblemExtensions.ToRpcException"/>
/// and <see cref="Norse.Infrastructure.Web.Client.Grpc.RpcExceptionExtensions.DecodeProblem"/> — named
/// distinctly only to avoid an ambiguous-extension-method compile error from having both
/// <c>Infrastructure.Web.Server</c> and <c>Infrastructure.Web.Client</c> referenced in one test project
/// (test-only, does not violate the WASM-safety rule that keeps production code from referencing both).
/// </summary>
static class TestExtensions
{
	public static RpcException ToRpcExceptionForTest(this Problem problem) =>
		Infrastructure.Web.Server.Mediator.Grpc.ProblemExtensions.ToRpcException(problem);

	public static Problem DecodeProblemForTest(this RpcException exception) =>
		Infrastructure.Web.Client.Grpc.RpcExceptionExtensions.DecodeProblem(exception);
}
