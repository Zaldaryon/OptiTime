param([string]$Runner = '', [switch]$VisualOnly)
$ErrorActionPreference = 'Stop'
if (-not $Runner) {
    $Runner = Join-Path $env:ProgramFiles 'Microsoft Visual Studio/18/Community/Common7/IDE/Extensions/TestPlatform/vstest.console.exe'
}
if (-not (Test-Path -LiteralPath $Runner)) { throw 'Provide the installed vstest.console.exe path.' }
if (-not $env:VINTAGE_STORY) { throw 'Set VINTAGE_STORY to the Vintage Story installation used to build this suite.' }
if (-not (Test-Path -LiteralPath (Join-Path $env:VINTAGE_STORY 'VintagestoryAPI.dll'))) { throw 'VINTAGE_STORY does not contain VintagestoryAPI.dll.' }
$assembly = Join-Path $PSScriptRoot 'bin/Release/net10.0/OptiTime.Pharos.Tests.dll'
if (-not (Test-Path -LiteralPath $assembly)) { throw 'Build the suite through build.bat first.' }
if (Get-Process Vintagestory -ErrorAction SilentlyContinue) { throw 'Close the native game before Pharos measurements.' }
$previousGamePath = $env:VINTAGE_STORY
$previousReference = $env:OPTITIME_PHAROS_REFERENCE
$env:OPTITIME_PHAROS_REFERENCE = $null
$results = Join-Path $PSScriptRoot ('TestResults/controlled-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $results -Force | Out-Null
$controlArtifacts = Join-Path $PSScriptRoot 'bin/Release/net10.0/artifacts/visual-control'
if (Test-Path -LiteralPath $controlArtifacts) { Copy-Item -LiteralPath $controlArtifacts -Destination (Join-Path $results 'prior-visual-controls') -Recurse }
$runs = @(
    @{ Name = 'fresh-vanilla'; Filter = 'FullyQualifiedName~FreshVanillaVisualControl' },
    @{ Name = 'fresh-mod'; Filter = 'FullyQualifiedName~FreshModVisualControl' },
    @{ Name = 'regressions'; Filter = 'FullyQualifiedName!~VanillaBaselineCapture&FullyQualifiedName!~BootVanillaPerformanceTests&FullyQualifiedName!~BootDisabledPerformanceTests&FullyQualifiedName!~BootEnabledPerformanceTests&FullyQualifiedName!~FreshVanillaVisualControl&FullyQualifiedName!~FreshModVisualControl' }
)
if ($VisualOnly) {
    $runs[2].Filter = 'FullyQualifiedName~OptiTimeRenderingFidelity|FullyQualifiedName~SceneCoverageTests'
    $runs += @{ Name = 'fresh-vanilla-repeat'; Filter = 'FullyQualifiedName~FreshVanillaVisualControl' }
}
$boots = @('BootVanillaPerformanceTests','BootDisabledPerformanceTests','BootEnabledPerformanceTests','BootEnabledPerformanceTests','BootDisabledPerformanceTests','BootVanillaPerformanceTests')
for ($index = 0; -not $VisualOnly -and $index -lt $boots.Count; $index++) {
    $runs += @{ Name = "performance-$index"; Filter = 'FullyQualifiedName~' + $boots[$index] }
}
$summary = @()
try {
foreach ($run in $runs) {
    Write-Output "Starting Pharos $($run.Name)"
    $output = Join-Path $results ($run.Name + '.log')
    & $Runner $assembly "/TestCaseFilter:$($run.Filter)" "/Logger:trx;LogFileName=$($run.Name).trx" "/ResultsDirectory:$results" *> $output
    $exitCode = $LASTEXITCODE
    $trx = Join-Path $results ($run.Name + '.trx')
    if (Test-Path -LiteralPath $trx) {
        [xml]$record = Get-Content -LiteralPath $trx -Raw
        $counters = $record.TestRun.ResultSummary.Counters
        $summary += [pscustomobject]@{Run=$run.Name;ExitCode=$exitCode;Total=[int]$counters.total;Passed=[int]$counters.passed;Failed=[int]$counters.failed}
        Write-Output "$($run.Name): total=$($counters.total), passed=$($counters.passed), failed=$($counters.failed), exit=$exitCode"
    } else {
        $summary += [pscustomobject]@{Run=$run.Name;ExitCode=$exitCode;Total=0;Passed=0;Failed=0}
        Write-Warning "$($run.Name) produced no TRX; inspect $output"
    }
    if ($run.Name -eq 'fresh-vanilla-repeat') {
        Copy-Item -LiteralPath $controlArtifacts -Destination (Join-Path $results 'scene-vanilla-repeat') -Recurse
    }
    if ($run.Name -eq 'fresh-mod') {
        Copy-Item -LiteralPath $controlArtifacts -Destination (Join-Path $results 'scene-mod') -Recurse
    }
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $results 'summary.json') -Encoding utf8NoBOM
    if ($run.Name -eq 'fresh-vanilla' -and $exitCode -eq 0 -and [int]$counters.passed -gt 0) {
        $reference = Join-Path $results 'scene-reference'
        Copy-Item -LiteralPath $controlArtifacts -Destination $reference -Recurse
        $env:OPTITIME_PHAROS_REFERENCE = $reference
    }
}
} finally {
    $env:OPTITIME_PHAROS_REFERENCE = $previousReference
    $env:VINTAGE_STORY = $previousGamePath
}
Write-Output "Pharos validation complete: $results"
if (@($summary | Where-Object { $_.ExitCode -ne 0 -or $_.Total -eq 0 -or $_.Failed -gt 0 }).Count) {
    throw "Pharos validation contains failed or missing tests; inspect $results"
}
