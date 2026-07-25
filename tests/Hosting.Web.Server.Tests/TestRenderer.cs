using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;

namespace Norse.Hosting.Web.Server.Tests;

/// <summary>
/// The thinnest possible concrete <see cref="Renderer"/> — <c>ComponentStatePersistenceManager.PersistStateAsync</c>
/// dispatches its pause-and-persist work through <c>renderer.Dispatcher</c>, so exercising the real,
/// public persistence pipeline in a test (rather than reaching for the framework's internal-only
/// shortcuts) requires a real <see cref="Renderer"/> instance. No component is ever rendered through
/// it here — the three overrides exist only to satisfy the abstract base, never to be exercised.
///
/// BL0006 ("types in Microsoft.AspNetCore.Components.RenderTree are not recommended for use outside
/// of the Blazor framework") is suppressed deliberately: this is exactly the narrow, test-only
/// exception the framework's own internal test suite makes for itself — there is no public,
/// non-<c>RenderTree</c> way to obtain a working <see cref="Dispatcher"/> for
/// <c>ComponentStatePersistenceManager.PersistStateAsync</c> to dispatch through.
/// </summary>
#pragma warning disable BL0006
sealed class TestRenderer() : Renderer(EmptyServiceProvider.Instance, NullLoggerFactory.Instance)
{
	public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

	protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

	protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
}
#pragma warning restore BL0006

/// <summary>A service provider with nothing registered — <see cref="TestRenderer"/> never resolves a service.</summary>
sealed class EmptyServiceProvider : IServiceProvider
{
	public static readonly EmptyServiceProvider Instance = new();

	public object? GetService(Type serviceType) => null;
}
