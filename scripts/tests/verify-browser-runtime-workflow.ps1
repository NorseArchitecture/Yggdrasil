[CmdletBinding()]
param(
	[string]$WorkflowPath
)

$ErrorActionPreference = 'Stop'
$workflowPath = if ([string]::IsNullOrWhiteSpace($WorkflowPath)) {
	Join-Path $PSScriptRoot '../../.github/workflows/browser-runtime.yml'
}
else {
	$WorkflowPath
}

if (-not (Test-Path $workflowPath)) {
	throw 'Yggdrasil browser-runtime.yml does not exist.'
}

$workflow = Get-Content $workflowPath -Raw

function Require-ExactLine([string]$block, [string]$line, [string]$scope) {
	if (-not [regex]::IsMatch($block, "(?m)^$([regex]::Escape($line))\r?`$")) {
		throw "$scope must contain exact active command: $line"
	}
}

function Require-FiveMinuteTimeout([string]$block, [string]$stepName) {
	if (-not [regex]::IsMatch($block, '(?m)^        timeout-minutes: 5\r?$')) {
		throw "$stepName must have a five-minute timeout."
	}
}

function Get-StepBlock([string]$jobBlock, [string]$stepName) {
	$match = [regex]::Match(
		$jobBlock,
		"(?ms)^      - name: $([regex]::Escape($stepName))\r?\n(?<block>.*?)(?=^      - name:|\z)")
	if (-not $match.Success) {
		throw "browser-runtime.yml is missing the $stepName step."
	}

	return $match.Groups['block'].Value
}

function Get-ExecutableRunLines([string]$stepBlock, [string]$stepName) {
	$run = [regex]::Match($stepBlock, '(?ms)^        run: \|\r?\n(?<body>.*?)(?=^        [^\s]|\z)')
	if (-not $run.Success) {
		throw "$stepName must contain a run: | body."
	}

	$lines = [Collections.Generic.List[string]]::new()
	foreach ($line in ($run.Groups['body'].Value -split '\r?\n')) {
		if ([string]::IsNullOrWhiteSpace($line)) {
			continue
		}

		if (-not $line.StartsWith('          ', [StringComparison]::Ordinal)) {
			throw "$stepName run body contains malformed active content."
		}

		$activeLine = $line.Substring(10).TrimEnd()
		if ($activeLine.StartsWith('#', [StringComparison]::Ordinal)) {
			continue
		}

		$lines.Add($activeLine)
	}

	return $lines.ToArray()
}

function Require-ExactRunBody([string]$stepBlock, [string]$stepName, [string[]]$expectedLines) {
	$actualLines = @(Get-ExecutableRunLines $stepBlock $stepName)
	if ($actualLines.Count -ne $expectedLines.Count) {
		throw "$stepName run body must exactly match the required active command sequence."
	}

	for ($index = 0; $index -lt $expectedLines.Count; $index++) {
		if (-not [string]::Equals($actualLines[$index], $expectedLines[$index], [StringComparison]::Ordinal)) {
			throw "$stepName run body must exactly match the required active command sequence."
		}
	}
}

if (-not [regex]::IsMatch($workflow, '(?m)^on:\r?\n  pull_request:\r?\n    branches: \[master\]\r?\n  workflow_dispatch:\r?$')) {
	throw 'browser-runtime.yml must declare the active pull_request master and workflow_dispatch triggers.'
}

$jobs = [regex]::Match($workflow, '(?ms)^jobs:\r?\n(?<block>.*?)(?=^[^\s#]|\z)')
if (-not $jobs.Success) {
	throw 'browser-runtime.yml is missing the jobs block.'
}

$jobHeaders = @([regex]::Matches($jobs.Groups['block'].Value, '(?m)^  (?<name>[A-Za-z0-9_-]+):\r?$'))
if ($jobHeaders.Count -ne 1 -or $jobHeaders[0].Groups['name'].Value -ne 'browser-runtime') {
	throw 'browser-runtime.yml must contain exactly one job named browser-runtime.'
}

$browserRuntimeJob = [regex]::Match($jobs.Groups['block'].Value, '(?ms)^  browser-runtime:\r?\n(?<block>.*?)(?=^  [^\s]|\z)')
if (-not $browserRuntimeJob.Success) {
	throw 'browser-runtime.yml is missing the browser-runtime job.'
}

if (-not [regex]::IsMatch($browserRuntimeJob.Groups['block'].Value, '(?m)^    timeout-minutes: 10\r?$')) {
	throw 'browser-runtime job must have a ten-minute timeout.'
}

$webBuild = 'dotnet build tests/Hosting.Web.Server.Tests/Hosting.Web.Server.Tests.csproj -c Release -p:UseProjectReferences=false'
$storiesBuild = 'dotnet build tests/Hosting.Stories.Tests/Hosting.Stories.Tests.csproj -c Release -p:UseProjectReferences=false'
$build = Get-StepBlock $browserRuntimeJob.Groups['block'].Value 'Build browser hosts'
Require-FiveMinuteTimeout $build 'Build browser hosts'
Require-ExactRunBody $build 'Build browser hosts' @($webBuild, $storiesBuild)

$install = Get-StepBlock $browserRuntimeJob.Groups['block'].Value 'Install Chromium'
Require-FiveMinuteTimeout $install 'Install Chromium'
Require-ExactLine $install '        shell: pwsh' 'Install Chromium'
Require-ExactLine $install '          & $script.FullName install --with-deps chromium' 'Install Chromium'
$installInvocations = @(Get-ExecutableRunLines $install 'Install Chromium' | Where-Object { $_ -match '^& \$script\.FullName install(?:\s|$)' })
if ($installInvocations.Count -ne 1) {
	throw 'Install Chromium run body must contain exactly one browser install invocation.'
}

if (-not [string]::Equals($installInvocations[0], '& $script.FullName install --with-deps chromium', [StringComparison]::Ordinal)) {
	throw 'Install Chromium must use the exact Chromium-only browser install invocation.'
}

$webTest = 'dotnet test tests/Hosting.Web.Server.Tests/Hosting.Web.Server.Tests.csproj -c Release -p:UseProjectReferences=false --no-build -- --explicit only --filter-class "*.WebServerBrowserRuntimeSmokeTests"'
$storiesTest = 'dotnet test tests/Hosting.Stories.Tests/Hosting.Stories.Tests.csproj -c Release -p:UseProjectReferences=false --no-build -- --explicit only --filter-class "*.StoriesBrowserRuntimeSmokeTests"'
$test = Get-StepBlock $browserRuntimeJob.Groups['block'].Value 'Test browser hosts'
Require-FiveMinuteTimeout $test 'Test browser hosts'
Require-ExactRunBody $test 'Test browser hosts' @($webTest, $storiesTest)

if ([regex]::IsMatch($workflow, '(?m)^ *(?:- +)?(?:"(?:run|uses)"|''(?:run|uses)'') *:')) {
	throw 'browser-runtime.yml must use only bare run and uses keys.'
}

$activeRunKeys = @([regex]::Matches($workflow, '(?m)^ *(?:- +)?run:[^\r\n]*\r?$'))
if ($activeRunKeys.Count -ne 3 -or @($activeRunKeys | Where-Object { $_.Value.TrimEnd() -ne '        run: |' }).Count -ne 0) {
	throw 'browser-runtime.yml must contain exactly three active literal run blocks and no other active run keys.'
}

$expectedDotnetCommands = @($webBuild, $storiesBuild, $webTest, $storiesTest)
$activeDotnetCommands = @([regex]::Matches($workflow, '(?m)^          (?<command>dotnet (?:build|test)\b[^\r\n]*)\r?$') | ForEach-Object { $_.Groups['command'].Value })
if ($activeDotnetCommands.Count -ne $expectedDotnetCommands.Count) {
	throw 'browser-runtime.yml must contain exactly the four active dotnet build/test commands bound to Build and Test steps.'
}

for ($index = 0; $index -lt $expectedDotnetCommands.Count; $index++) {
	if (-not [string]::Equals($activeDotnetCommands[$index], $expectedDotnetCommands[$index], [StringComparison]::Ordinal)) {
		throw 'browser-runtime.yml must contain exactly the four active dotnet build/test commands bound to Build and Test steps.'
	}
}

$activeInstallInvocations = @([regex]::Matches($workflow, '(?m)^          & \$script\.FullName install(?:\s|$)[^\r\n]*\r?$'))
if ($activeInstallInvocations.Count -ne 1) {
	throw 'browser-runtime.yml must contain exactly one active Playwright install invocation.'
}

$expectedActionUses = @(
	'        uses: actions/checkout@v7',
	'        uses: actions/setup-dotnet@v6',
	'        uses: actions/upload-artifact@v7'
)
$activeActionUses = @(
	[regex]::Matches($workflow, '(?m)^(?<line> *(?:- +)?uses:[^\r\n]*)\r?$') |
		ForEach-Object { $_.Groups['line'].Value }
)
if ($activeActionUses.Count -ne $expectedActionUses.Count) {
	throw 'browser-runtime.yml must contain exactly the three required active action uses.'
}

for ($index = 0; $index -lt $expectedActionUses.Count; $index++) {
	if (-not [string]::Equals($activeActionUses[$index], $expectedActionUses[$index], [StringComparison]::Ordinal)) {
		throw 'browser-runtime.yml must contain exactly the three required active action uses.'
	}
}

$upload = Get-StepBlock $browserRuntimeJob.Groups['block'].Value 'Upload Playwright failure evidence'
Require-ExactLine $upload '        if: failure()' 'Upload Playwright failure evidence'
Require-ExactLine $upload '        uses: actions/upload-artifact@v7' 'Upload Playwright failure evidence'
Require-ExactLine $upload "          path: '**/TestResults/playwright/**'" 'Upload Playwright failure evidence'
Require-ExactLine $upload '          if-no-files-found: ignore' 'Upload Playwright failure evidence'
Require-ExactLine $upload '          retention-days: 7' 'Upload Playwright failure evidence'

foreach ($forbidden in @('--coverage', '-m:1', 'firefox', 'webkit')) {
	if ($workflow.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) {
		throw "browser-runtime.yml contains forbidden contract: $forbidden"
	}
}
