using System.Diagnostics.CodeAnalysis;

namespace Norse.Hosting.Stories.Tests.BrowserRuntime;

[CollectionDefinition(Name, DisableParallelization = true)]
[SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix",
	Justification = "xUnit collection fixture naming convention")]
public sealed class StoriesBrowserCollection : ICollectionFixture<StoriesBrowserFixture>
{
	public const string Name = "StoriesBrowser";
}
