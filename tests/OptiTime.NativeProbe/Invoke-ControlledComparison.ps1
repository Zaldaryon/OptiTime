param(
    [string]$GamePath = $env:VINTAGE_STORY,
    [string]$SourceSave,
    [string[]]$Variants = @('vanilla', 'disabled', 'enabled', 'enabled', 'disabled', 'vanilla'),
    [switch]$CollectCpuTrace
)
$ErrorActionPreference = 'Stop'
if (-not $GamePath -or -not (Test-Path -LiteralPath (Join-Path $GamePath 'Vintagestory.exe'))) { throw 'Set VINTAGE_STORY or pass -GamePath to an installed Vintage Story directory.' }
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$data = Join-Path $env:APPDATA 'VintagestoryData'
$run = Join-Path $PSScriptRoot ('bin/Runs/controlled-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
if (-not $SourceSave -or -not (Test-Path -LiteralPath $SourceSave)) { throw 'Provide an existing dedicated creative profiling save.' }
if (Get-Process Vintagestory -ErrorAction SilentlyContinue) { throw 'Close the game gracefully before starting controlled measurements.' }
$snapshot = Join-Path $run 'source.vcdbs'
Copy-Item -LiteralPath $SourceSave -Destination $snapshot
$settings = Join-Path $data 'clientsettings.json'
$config = Join-Path $data 'ModConfig/OptiTime.json'
$backup = Join-Path $data ('OptiTimeProfileBackup-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backup | Out-Null
$hadConfig = Test-Path -LiteralPath $config
Copy-Item -LiteralPath $settings -Destination (Join-Path $backup 'clientsettings.json')
if ($hadConfig) { Copy-Item -LiteralPath $config -Destination (Join-Path $backup 'OptiTime.json') }
$packages = @(Get-ChildItem (Join-Path $data 'Mods') -Filter 'OptiTime*.zip')
# Standard Windows APIs keep measurement focus explicit.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ProfileFocus {
    [StructLayout(LayoutKind.Sequential)] public struct Message { public IntPtr hwnd; public uint message; public UIntPtr wparam; public IntPtr lparam; public uint time; public int x; public int y; public uint extra; }
    [DllImport("user32.dll")] public static extern bool PeekMessage(out Message message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
    public static IntPtr WindowWithTitle(string title) => FindWindow(null, title);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    public static void Focus(IntPtr hwnd) {
        Message message;
        PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
        uint current = GetCurrentThreadId();
        uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        uint target = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
        bool attached = current != foreground && AttachThreadInput(current, foreground, true);
        bool targetAttached = target != current && target != foreground && AttachThreadInput(current, target, true);
        try { ShowWindow(hwnd, 9); SetForegroundWindow(hwnd); }
        finally { if (targetAttached) AttachThreadInput(current, target, false); if (attached) AttachThreadInput(current, foreground, false); }
    }
}
'@
$process = $null
$focusProcess = $null
$activePackage = Join-Path $data 'Mods/OptiTime-1.5.16.zip'
$profilePackageStaged = $false
$previousProfile = $env:OPTITIME_NATIVE_PROFILE
$probeDirectory = Join-Path $data 'Mods/OptiTime.NativeProbe'
$hadProbeDirectory = Test-Path -LiteralPath $probeDirectory
$probeFiles = @('OptiTime.NativeProbe.dll', 'modinfo.json')
$probeStaged = $false
try {
    $focusProcess = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + (Join-Path $PSScriptRoot 'Show-FocusWindow.ps1') + '"')) -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $run 'focus-window-error.log')
    $focusDeadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        $focusTarget = [ProfileFocus]::WindowWithTitle(('OptiTime benchmark focus target ' + $focusProcess.Id))
        if ($focusTarget -ne [IntPtr]::Zero) { break }
        if ($focusProcess.HasExited -or $focusDeadline.Elapsed.TotalSeconds -gt 60) { throw 'Dedicated focus window did not initialize.' }
        Start-Sleep -Milliseconds 250
    } while ($true)
    $null = [ProfileFocus]::ShowWindow($focusTarget, 4)
    foreach ($package in $packages) { Move-Item -LiteralPath $package.FullName -Destination $backup }
    foreach ($name in $probeFiles) {
        $installedProbe = Join-Path $probeDirectory $name
        if (Test-Path -LiteralPath $installedProbe) { Copy-Item -LiteralPath $installedProbe -Destination (Join-Path $backup ('probe-' + $name)) }
    }
    New-Item -ItemType Directory -Path $probeDirectory -Force | Out-Null
    $probeStaged = $true
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin/ProbeMod/OptiTime.NativeProbe.dll'),(Join-Path $PSScriptRoot 'bin/ProbeMod/modinfo.json') -Destination (Join-Path $data 'Mods/OptiTime.NativeProbe') -Force
    for ($index = 0; $index -lt $Variants.Count; $index++) {
        $variant = $Variants[$index]
        if ($variant -notin @('vanilla','disabled','enabled')) { throw "Unknown variant $variant" }
        Copy-Item -LiteralPath (Join-Path $backup 'clientsettings.json') -Destination $settings -Force
        if ($profilePackageStaged -and (Test-Path -LiteralPath $activePackage)) { Remove-Item -LiteralPath $activePackage }
        $profilePackageStaged = $false
        if ($variant -ne 'vanilla') {
            Copy-Item -LiteralPath (Join-Path $root 'bin/OptiTime-1.5.16.zip') -Destination $activePackage
            $profilePackageStaged = $true
            $fixture = if ($variant -eq 'enabled') { 'AllEnabled.json' } else { 'AllDisabled.json' }
            Copy-Item -LiteralPath (Join-Path $root ('tests/OptiTime.Pharos.Tests/Fixtures/' + $fixture)) -Destination $config -Force
        }
        $world = 'OptiTimeControlled-' + (Split-Path $run -Leaf) + '-' + $index
        $save = Join-Path $data ('Saves/' + $world + '.vcdbs')
        Copy-Item -LiteralPath $snapshot -Destination $save
        $output = Join-Path $run ($index.ToString() + '-' + $variant + '.json')
        $env:OPTITIME_NATIVE_PROFILE = $output
        $process = Start-Process -FilePath (Join-Path $GamePath 'Vintagestory.exe') -ArgumentList @('--openWorld', $world) -WindowStyle Hidden -PassThru
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $currentMode = ''
        Write-Output "Native boot $index/$variant started; PID $($process.Id)"
        while (-not $process.HasExited -and -not (Test-Path -LiteralPath $output) -and $watch.Elapsed.TotalSeconds -lt 360) {
            $process.Refresh()
            $phaseFile = $output + '.phase.json'
            if (Test-Path -LiteralPath $phaseFile) {
                try { $mode = (Get-Content -LiteralPath $phaseFile -Raw | ConvertFrom-Json).mode } catch { $mode = $currentMode }
                if ($mode -ne $currentMode) {
                    $currentMode = $mode
                    Write-Output "Native boot $index/$variant phase $mode"
                }
                if ($mode -eq 'background') {
                    if (-not [ProfileFocus]::IsIconic($process.MainWindowHandle)) { $null = [ProfileFocus]::ShowWindow($process.MainWindowHandle, 6) }
                    $focusProcess.Refresh()
                    if ($focusProcess.HasExited) { throw 'Dedicated focus window exited; inspect focus-window-error.log.' }
                    $focusTarget = [ProfileFocus]::WindowWithTitle(('OptiTime benchmark focus target ' + $focusProcess.Id))
                    if ($focusTarget -eq [IntPtr]::Zero) { throw 'Dedicated focus window is unavailable.' }
                    if ([ProfileFocus]::GetForegroundWindow() -ne $focusTarget) { [ProfileFocus]::Focus($focusTarget) }
                } elseif ($process.MainWindowHandle -ne [IntPtr]::Zero -and [ProfileFocus]::GetForegroundWindow() -ne $process.MainWindowHandle) {
                    [ProfileFocus]::Focus($process.MainWindowHandle)
                }
            }
            $null = $process.WaitForExit(1000)
        }
        if (-not (Test-Path -LiteralPath $output)) { throw "Native boot $index/$variant did not produce measurements within 360 seconds." }
        Copy-Item -LiteralPath (Join-Path $data 'Logs/client-main.log') -Destination (Join-Path $run ($index.ToString() + '-' + $variant + '-client.log'))
        if ($variant -ne 'vanilla') { Copy-Item -LiteralPath $config -Destination (Join-Path $run ($index.ToString() + '-' + $variant + '-config.json')) }
        if ($CollectCpuTrace) {
            [ProfileFocus]::Focus($process.MainWindowHandle)
            $null = $process.WaitForExit(3000)
            $trace = Join-Path $run ($index.ToString() + '-' + $variant + '.nettrace')
            Write-Output "Collecting separate .NET sampled-thread trace for $variant (outside timed frame measurements)"
            & dotnet-trace collect --process-id $process.Id --duration 00:00:15 --profile dotnet-sampled-thread-time --format Speedscope --output $trace *> ($trace + '.log')
            if ($LASTEXITCODE -ne 0) { throw "CPU trace failed; inspect $trace.log" }
        }
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(45000)) { throw 'Game did not close gracefully; stop before another boot.' }
        $process = $null
        $record = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
        foreach ($phase in $record.phases) {
            $focusCount = @($phase.samples | Where-Object Focused -EQ $true).Count
            Write-Output "$variant/$($phase.mode): $($phase.samples.Count) frames, $focusCount focused"
            $expectedFocus = if ($phase.mode -eq 'background') { 0 } else { $phase.samples.Count }
            if ($focusCount -ne $expectedFocus) { throw "Mixed focus invalidates $variant/$($phase.mode); rerun this boot." }
            if ($phase.samples.Count -lt 100) { throw 'Insufficient native frame samples.' }
            $cadence = @($phase.samples | Where-Object CadenceMs -GT 0)
            $duration = ($cadence | Measure-Object CadenceMs -Sum).Sum / 1000
            if ($phase.samples.Count - $cadence.Count -ne 1 -or [Math]::Abs($duration - $phase.seconds) -gt 0.2) {
                throw "Interrupted measurement window invalidates $variant/$($phase.mode)."
            }
            if ([Math]::Abs($phase.endHours - $phase.startHours) -gt 0.0001) { throw 'World time was not frozen.' }
            for ($coordinate = 0; $coordinate -lt 5; $coordinate++) {
                if ([Math]::Abs($phase.endPose[$coordinate] - $phase.startPose[$coordinate]) -gt 0.01) { throw 'Camera pose drift invalidates native comparison.' }
            }
        }
    }
    Write-Output "Native comparison complete: $run"
} finally {
    if ($process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        $null = $process.WaitForExit(45000)
    }
    if ($process -and -not $process.HasExited) {
        Write-Warning "Game still running. Restore backups from $backup after closing it; do not overwrite live settings."
    } else {
        if ($profilePackageStaged -and (Test-Path -LiteralPath $activePackage)) { Remove-Item -LiteralPath $activePackage }
        foreach ($package in $packages) {
            $savedPackage = Join-Path $backup $package.Name
            if (Test-Path -LiteralPath $savedPackage) { Move-Item -LiteralPath $savedPackage -Destination $package.FullName }
        }
        Copy-Item -LiteralPath (Join-Path $backup 'clientsettings.json') -Destination $settings -Force
        if ($hadConfig) { Copy-Item -LiteralPath (Join-Path $backup 'OptiTime.json') -Destination $config -Force }
        elseif (Test-Path -LiteralPath $config) { Remove-Item -LiteralPath $config }
        if ($probeStaged) {
            foreach ($name in $probeFiles) {
                $installedProbe = Join-Path $probeDirectory $name
                $savedProbe = Join-Path $backup ('probe-' + $name)
                if (Test-Path -LiteralPath $savedProbe) {
                    Copy-Item -LiteralPath $savedProbe -Destination $installedProbe -Force
                    Remove-Item -LiteralPath $savedProbe
                } elseif (Test-Path -LiteralPath $installedProbe) { Remove-Item -LiteralPath $installedProbe }
            }
            if (-not $hadProbeDirectory -and -not @(Get-ChildItem -LiteralPath $probeDirectory -Force).Count) {
                Remove-Item -LiteralPath $probeDirectory
            }
        }
        foreach ($name in $probeFiles) {
            $savedProbe = Join-Path $backup ('probe-' + $name)
            if (Test-Path -LiteralPath $savedProbe) { Remove-Item -LiteralPath $savedProbe }
        }
        # Remove only the explicit backup files; no recursive deletion of user data.
        Remove-Item -LiteralPath (Join-Path $backup 'clientsettings.json')
        if ($hadConfig) { Remove-Item -LiteralPath (Join-Path $backup 'OptiTime.json') }
        Remove-Item -LiteralPath $backup
    }
    $env:OPTITIME_NATIVE_PROFILE = $previousProfile
    if ($focusProcess -and -not $focusProcess.HasExited) {
        $focusTarget = [ProfileFocus]::WindowWithTitle(('OptiTime benchmark focus target ' + $focusProcess.Id))
        if ($focusTarget -ne [IntPtr]::Zero) { $null = [ProfileFocus]::PostMessage($focusTarget, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }
        else { $null = $focusProcess.CloseMainWindow() }
        $null = $focusProcess.WaitForExit(10000)
    }
}
