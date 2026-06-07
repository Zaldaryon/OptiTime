param(
    [string]$GamePath,
    [switch]$SkipBuild,
    [switch]$RequireRecentLog,
    [int]$RecentLogHours = 24
)

if ($PSVersionTable.PSVersion.Major -lt 7) {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if (-not $pwsh) {
        throw "PowerShell 7 (pwsh) is required to run Invoke-SmokeTests.ps1"
    }

    & $pwsh.Source -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @args
    exit $LASTEXITCODE
}

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param(
        [string]$Name,
        [string]$Status,
        [string]$Detail
    )

    $results.Add([pscustomobject]@{
        Name = $Name
        Status = $Status
        Detail = $Detail
    })
}

function Get-GamePath {
    param([string]$ConfiguredPath)

    $candidates = New-Object System.Collections.Generic.List[string]
    if ($ConfiguredPath) {
        $candidates.Add($ConfiguredPath)
    }

    if ($env:VINTAGE_STORY) {
        $candidates.Add($env:VINTAGE_STORY)
    }

    $documents = [Environment]::GetFolderPath("MyDocuments")
    if ($documents) {
        $candidates.Add((Join-Path $documents "Misc\\Vintagestory"))
        $candidates.Add((Join-Path $documents "Games\\Vintagestory"))
    }

    foreach ($candidate in $candidates) {
        if (-not $candidate) { continue }
        $exePath = Join-Path $candidate "Vintagestory.exe"
        if (Test-Path $exePath) {
            return (Resolve-Path $candidate).Path
        }
    }

    return $null
}

function Test-ZipContains {
    param(
        [string]$ZipPath,
        [string]$EntrySuffix
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.Replace("/", "\").EndsWith($EntrySuffix, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }

        return $false
    }
    finally {
        $archive.Dispose()
    }
}

function Scan-IlPattern {
    param(
        [byte[]]$Bytes,
        [byte[]]$Pattern
    )

    if ($null -eq $Bytes -or $null -eq $Pattern -or $Bytes.Length -lt $Pattern.Length) {
        return $false
    }

    for ($i = 0; $i -le $Bytes.Length - $Pattern.Length; $i++) {
        $matched = $true
        for ($j = 0; $j -lt $Pattern.Length; $j++) {
            if ($Bytes[$i + $j] -ne $Pattern[$j]) {
                $matched = $false
                break
            }
        }

        if ($matched) {
            return $true
        }
    }

    return $false
}

function Get-LatestLog {
    $logDirs = @(
        (Join-Path $env:APPDATA "VintagestoryData\\Logs"),
        ":APPDATA\VintagestoryData\Logs",
        ":APPDATA\VintagestoryData\Logs"
    ) | Select-Object -Unique

    $logs = foreach ($dir in $logDirs) {
        if (Test-Path $dir) {
            Get-ChildItem $dir -Filter *.log -ErrorAction SilentlyContinue
        }
    }

    return $logs | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

Push-Location $repoRoot
try {
    $csproj = [xml](Get-Content (Join-Path $repoRoot "OptiTime.csproj"))
    $modinfo = Get-Content (Join-Path $repoRoot "modinfo.json") | ConvertFrom-Json
    $version = [string]$modinfo.version
    $projectVersion = [string]$csproj.Project.PropertyGroup.Version

    if ($version -eq $projectVersion) {
        Add-Result "Version sync" "PASS" "modinfo.json and OptiTime.csproj both use $version"
    }
    else {
        Add-Result "Version sync" "FAIL" "modinfo.json=$version csproj=$projectVersion"
    }

    if (-not $SkipBuild) {
        $buildOutput = & cmd.exe /c (Join-Path $repoRoot "build.bat") 2>&1
        if ($LASTEXITCODE -eq 0) {
            Add-Result "Build/package" "PASS" (($buildOutput | Select-Object -Last 3) -join " | ")
        }
        else {
            Add-Result "Build/package" "FAIL" (($buildOutput | Select-Object -Last 8) -join " | ")
        }
    }
    else {
        Add-Result "Build/package" "WARN" "Skipped by request"
    }

    $zipName = "OptiTime-$version.zip"
    $zipPath = Join-Path $repoRoot "bin\$zipName"
    $installedZipPath = Join-Path $env:APPDATA "VintagestoryData\Mods\$zipName"
    $dllPath = Join-Path $repoRoot "bin\Release\net8.0\OptiTime.dll"

    Add-Result "Packaged zip exists" ($(if (Test-Path $zipPath) { "PASS" } else { "FAIL" })) $zipPath
    Add-Result "Installed zip exists" ($(if (Test-Path $installedZipPath) { "PASS" } else { "FAIL" })) $installedZipPath
    Add-Result "Build output DLL exists" ($(if (Test-Path $dllPath) { "PASS" } else { "FAIL" })) $dllPath

    if (Test-Path $zipPath) {
        Add-Result "Zip contains OptiTime.dll" ($(if (Test-ZipContains -ZipPath $zipPath -EntrySuffix "OptiTime.dll") { "PASS" } else { "FAIL" })) $zipName
        Add-Result "Zip contains modinfo.json" ($(if (Test-ZipContains -ZipPath $zipPath -EntrySuffix "modinfo.json") { "PASS" } else { "FAIL" })) $zipName
        Add-Result "Zip contains assets" ($(if (Test-ZipContains -ZipPath $zipPath -EntrySuffix "assets\optitime\lang\pt-br.json") { "PASS" } else { "FAIL" })) $zipName
    }

    $resolvedGamePath = Get-GamePath -ConfiguredPath $GamePath
    if (-not $resolvedGamePath) {
        Add-Result "Game path" "FAIL" "Could not resolve Vintage Story install path"
    }
    else {
        Add-Result "Game path" "PASS" $resolvedGamePath

        $apiPath = Join-Path $resolvedGamePath "VintagestoryAPI.dll"
        $libPath = Join-Path $resolvedGamePath "VintagestoryLib.dll"
        $harmonyPath = Join-Path $resolvedGamePath "Lib\0Harmony.dll"
        $survivalPath = Join-Path $resolvedGamePath "Mods\VSSurvivalMod.dll"

        $harmonyAssembly = $null
        $apiAssembly = $null
        $libAssembly = $null
        $survivalAssembly = $null

        if (Test-Path $harmonyPath) { $harmonyAssembly = [System.Reflection.Assembly]::LoadFrom($harmonyPath) }
        if (Test-Path $apiPath) { $apiAssembly = [System.Reflection.Assembly]::LoadFrom($apiPath) }
        if (Test-Path $libPath) { $libAssembly = [System.Reflection.Assembly]::LoadFrom($libPath) }
        if (Test-Path $survivalPath) { $survivalAssembly = [System.Reflection.Assembly]::LoadFrom($survivalPath) }

        if (Test-Path $dllPath) {
            $modAssembly = [System.Reflection.Assembly]::LoadFrom($dllPath)
            Add-Result "Mod assembly load" "PASS" $dllPath
            $reflectionScriptPath = Join-Path $repoRoot "scripts\Invoke-ReflectionSmoke.ps1"
            $pwshPath = (Get-Command pwsh -ErrorAction Stop).Source
            $reflectionOutput = & $pwshPath -NoProfile -ExecutionPolicy Bypass -File $reflectionScriptPath -GamePath $resolvedGamePath -DllPath $dllPath 2>&1
            $reflectionExit = $LASTEXITCODE

            foreach ($line in $reflectionOutput) {
                if ($line -match '^[^|]+\|(True|False)\|') {
                    $parts = $line -split '\|', 3
                    Add-Result $parts[0] ($(if ($parts[1] -eq "True") { "PASS" } else { "FAIL" })) $parts[2]
                }
            }

            if ($reflectionExit -ne 0 -and -not ($reflectionOutput | Where-Object { $_ -match '^[^|]+\|(True|False)\|' })) {
                Add-Result "Reflection smoke" "FAIL" (($reflectionOutput | Select-Object -First 5) -join " | ")
            }
        }
        else {
            Add-Result "Mod assembly load" "FAIL" "Missing built DLL at $dllPath"
        }
    }

    $latestLog = Get-LatestLog
    if (-not $latestLog) {
        $status = if ($RequireRecentLog) { "FAIL" } else { "PASS" }
        $detail = if ($RequireRecentLog) {
            "No Vintagestory log file found"
        }
        else {
            "No Vintagestory log file found; optional log checks skipped"
        }

        Add-Result "Log scan" $status $detail
    }
    else {
        $ageHours = ((Get-Date) - $latestLog.LastWriteTime).TotalHours
        if ($ageHours -gt $RecentLogHours) {
            $status = if ($RequireRecentLog) { "FAIL" } else { "WARN" }
            Add-Result "Log recency" $status "$($latestLog.FullName) is older than $RecentLogHours hours"
        }
        else {
            Add-Result "Log recency" "PASS" "$($latestLog.FullName) updated $([math]::Round($ageHours, 2)) hours ago"
        }

        $logContent = Get-Content $latestLog.FullName
        $optitimeLines = @($logContent | Select-String -Pattern "OptiTime")
        if ($optitimeLines.Count -gt 0) {
            Add-Result "OptiTime log presence" "PASS" "$($optitimeLines.Count) OptiTime lines in latest log"
        }
        else {
            Add-Result "OptiTime log presence" "WARN" "Latest log does not contain OptiTime lines"
        }

        $problemPatterns = @(
            "OptiTime.*FAILED",
            "FAILED to find IL pattern",
            "Error during handbook indexing",
            "Recipe lookup optimization skipped",
            "Chunk tesselation optimization skipped",
            "Failed to load .* optimization",
            "OptiTime.*Exception",
            "OptiTime.*Error"
        )

        $problemLines = @($logContent | Select-String -Pattern $problemPatterns)
        if ($problemLines.Count -gt 0) {
            $sample = ($problemLines | Select-Object -First 3 | ForEach-Object { $_.Line.Trim() }) -join " | "
            Add-Result "OptiTime log errors" "FAIL" $sample
        }
        else {
            Add-Result "OptiTime log errors" "PASS" "No matching OptiTime error patterns in latest log"
        }
    }

    $statusOrder = @{ PASS = 0; WARN = 1; FAIL = 2 }
    $sorted = $results | Sort-Object { $statusOrder[$_.Status] }, Name
    $sorted | Format-Table -AutoSize

    $failCount = @($results | Where-Object { $_.Status -eq "FAIL" }).Count
    $warnCount = @($results | Where-Object { $_.Status -eq "WARN" }).Count
    Write-Host ""
    Write-Host ("Summary: {0} pass, {1} warn, {2} fail" -f (($results | Where-Object { $_.Status -eq "PASS" }).Count), $warnCount, $failCount)

    if ($failCount -gt 0) {
        exit 1
    }
}
finally {
    Pop-Location
}
