namespace Norse.DiscoveryFixture;

/// <summary>
/// Fixture-local request record — exists only so <see cref="FixtureRequestValidator"/> has a
/// concrete <c>T</c> to validate. This whole project's purpose is being a real, separately compiled
/// assembly the generated client-side discovery (Task 14) can find when referenced by a consumer
/// other than the hosts themselves — never referenced by <c>Hosting.Web.Client</c>/
/// <c>Hosting.Web.Server</c> directly.
/// </summary>
public sealed record FixtureRequest
{
	/// <summary>The one field this fixture validates.</summary>
	public required string Name { get; init; }
}
