using System.Diagnostics.CodeAnalysis;

namespace Norse.Hosting.Web.Server.Tests.BrowserRuntime;

[CollectionDefinition(Name, DisableParallelization = true)]
[SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix",
	Justification = "xUnit collection fixture naming convention")]
public sealed class WebServerBrowserCollection : ICollectionFixture<WebServerBrowserFixture>
{
	public const string Name = "WebServerBrowser";
}
