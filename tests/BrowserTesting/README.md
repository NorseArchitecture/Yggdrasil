# Browser runtime smoke gate

The Yggdrasil browser-runtime workflow is a released-package gate. Every build and test command sets `-p:UseProjectReferences=false`, which resolves the standalone Yggdrasil NuGet graph rather than consuming sibling realm projects from the Bifrost filesystem. The Release build produces the `playwright.ps1` used to install the matching browser runtime, so build, browser installation, and test execution remain coupled to the same package graph.

The workflow has a ten-minute hard ceiling and separate five-minute ceilings for the Build browser hosts, Install Chromium, and Test browser hosts diagnostic legs. Those phase ceilings are deliberately non-additive: they identify which leg hung, while the job ceiling remains the platform's ratchet for the complete gate. The Test browser hosts ceiling covers both sequential smoke commands together; it is not a five-minute allowance for each host. It runs Chromium only. Firefox and WebKit are intentionally deferred.

## Local equivalent

Run these commands from the `Yggdrasil` repository root, in this order:

```text
dotnet build tests/Hosting.Web.Server.Tests/Hosting.Web.Server.Tests.csproj -c Release -p:UseProjectReferences=false
dotnet build tests/Hosting.Stories.Server.Tests/Hosting.Stories.Server.Tests.csproj -c Release -p:UseProjectReferences=false
pwsh tests/Hosting.Web.Server.Tests/bin/Release/net11.0/playwright.ps1 install chromium
dotnet test tests/Hosting.Web.Server.Tests/Hosting.Web.Server.Tests.csproj -c Release -p:UseProjectReferences=false --no-build -- --explicit only --filter-class "*.WebServerBrowserRuntimeSmokeTests"
dotnet test tests/Hosting.Stories.Server.Tests/Hosting.Stories.Server.Tests.csproj -c Release -p:UseProjectReferences=false --no-build -- --explicit only --filter-class "*.StoriesBrowserRuntimeSmokeTests"
```

The browser smoke classes and the real-host fixture are xUnit explicit tests: ordinary `dotnet test` selections skip them and do not start their Kestrel host or launch Chromium. Include `--explicit only` and the matching class filter to run the smoke classes deliberately. The ordinary `BrowserProcessLeaseTests` unit test intentionally acquires and releases its file lease twice to test that primitive; it has no browser prerequisite and is not a host-fixture or Chromium launch.

On a browser-test failure, evidence is written beneath `**/TestResults/playwright/**`. After the cross-process lease is held and before a named evidence run starts, that run removes only its own exact prior `TestResults/playwright/<test-name>` directory. Sibling test evidence and the shared artifact root are preserved, and a cleanup failure aborts startup loudly. This prevents a later successful run from leaving its own stale failure bundle uploadable. The workflow uploads the evidence root only when a prior step has failed; successful runs intentionally leave no evidence artifact for the tests they ran.
