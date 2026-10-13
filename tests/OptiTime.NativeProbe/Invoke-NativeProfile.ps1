param(
    [ValidateSet('vanilla', 'disabled', 'enabled')]
    [string]$Variant = 'vanilla',
    [string]$GamePath = $env:VINTAGE_STORY
)
$ErrorActionPreference = 'Stop'
if (-not $GamePath -or -not (Test-Path -LiteralPath (Join-Path $GamePath 'Vintagestory.exe'))) { throw 'Set VINTAGE_STORY or pass -GamePath to an installed Vintage Story directory.' }
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$run = Join-Path $root ('tests\OptiTime.NativeProbe\bin\Runs\' + $Variant + '-' + [guid]::NewGuid().ToString('N'))
$mods = Join-Path $run 'Mods'
New-Item -ItemType Directory -Path $mods -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'bin\ProbeMod') (Join-Path $mods 'NativeProbe') -Recurse
if ($Variant -ne 'vanilla') {
    Copy-Item (Join-Path $root 'bin\OptiTime-1.5.16.zip') $mods
    $configDir = Join-Path $run 'ModConfig'
    New-Item -ItemType Directory -Path $configDir | Out-Null
    $fixture = if ($Variant -eq 'disabled') { 'AllDisabled.json' } else { 'AllEnabled.json' }
    Copy-Item (Join-Path $root ('tests\OptiTime.Pharos.Tests\Fixtures\' + $fixture)) (Join-Path $configDir 'OptiTime.json')
}
# Fresh data path. Never import account credentials or touch normal game saves/settings.
$arguments = '--dataPath "' + $run + '" --openWorld NativeProfile --playStyle creativebuilding'
$process = Start-Process (Join-Path $GamePath 'Vintagestory.exe') -ArgumentList $arguments -PassThru
$watch = [Diagnostics.Stopwatch]::StartNew()
$result = Join-Path $run 'native-probe.json'
try {
    while (-not $process.HasExited -and -not (Test-Path $result) -and $watch.Elapsed.TotalSeconds -lt 240) {
        $null = $process.WaitForExit(500)
        $process.Refresh()
    }
    if (-not (Test-Path $result)) {
        throw "Native probe produced no measurements. Isolated launch may require interactive login; inspect sandbox logs. Data path: $run"
    }
    Write-Output "Native measurements: $result"
    Write-Output 'Fresh world, uncontrolled focus/settings: capability probe only, not a performance-benefit comparison.'
} finally {
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(15000)) {
            # Only this script's newly launched sandbox process.
            Stop-Process -Id $process.Id
        }
    }
}
