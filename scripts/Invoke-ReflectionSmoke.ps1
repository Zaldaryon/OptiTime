param(
    [Parameter(Mandatory = $true)]
    [string]$GamePath,

    [Parameter(Mandatory = $true)]
    [string]$DllPath
)

if ($PSVersionTable.PSVersion.Major -lt 7) {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if (-not $pwsh) {
        throw "PowerShell 7 (pwsh) is required to run Invoke-ReflectionSmoke.ps1"
    }

    & $pwsh.Source -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @args
    exit $LASTEXITCODE
}

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Emit-Check($Name, $Ok, $Detail) {
    "{0}|{1}|{2}" -f $Name, $Ok, $Detail
}

$api = [System.Reflection.Assembly]::LoadFrom((Join-Path $GamePath "VintagestoryAPI.dll"))
$lib = [System.Reflection.Assembly]::LoadFrom((Join-Path $GamePath "VintagestoryLib.dll"))
$survival = [System.Reflection.Assembly]::LoadFrom((Join-Path $GamePath "Mods\VSSurvivalMod.dll"))
$mod = [System.Reflection.Assembly]::LoadFrom($DllPath)

$publicInstance = [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Instance
$nonPublicInstance = [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Instance
$anyStatic = [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static

$failed = 0

$grid = $api.GetType("Vintagestory.API.Common.GridRecipe")
$player = $api.GetType("Vintagestory.API.Common.IPlayer")
$slot = $api.GetType("Vintagestory.API.Common.ItemSlot")
$slots = if ($slot) { $slot.MakeArrayType() } else { $null }
$gridMatches = if ($grid -and $player -and $slots) { $grid.GetMethod("Matches", $publicInstance, $null, [Type[]]@($player, $slots, [int]), $null) } else { $null }
$gridShapeLess = if ($grid -and $slots) { $grid.GetMethod("MatchesShapeLess", $nonPublicInstance, $null, [Type[]]@($slots, [int]), $null) } else { $null }
Emit-Check "GridRecipe.Matches" ($null -ne $gridMatches) ([string]$gridMatches)
if (-not $gridMatches) { $failed++ }
Emit-Check "GridRecipe.MatchesShapeLess" ($null -ne $gridShapeLess) ([string]$gridShapeLess)
if (-not $gridShapeLess) { $failed++ }

$chunk = $lib.GetType("Vintagestory.Client.NoObf.ChunkTesselatorManager")
$onBefore = if ($chunk) { $chunk.GetMethod("OnBeforeFrame", [Type[]]@([single])) } else { $null }
$queue = if ($chunk) { $chunk.GetField("tessChunksQueue", $nonPublicInstance) } else { $null }
$prio = if ($chunk) { $chunk.GetField("tessChunksQueuePriority", $nonPublicInstance) } else { $null }
Emit-Check "ChunkTesselatorManager.OnBeforeFrame" ($null -ne $onBefore) ([string]$onBefore)
if (-not $onBefore) { $failed++ }
Emit-Check "ChunkTesselatorManager.tessChunksQueue" ($null -ne $queue) ($(if ($queue) { $queue.FieldType.FullName } else { "" }))
if (-not $queue) { $failed++ }
Emit-Check "ChunkTesselatorManager.tessChunksQueuePriority" ($null -ne $prio) ($(if ($prio) { $prio.FieldType.FullName } else { "" }))
if (-not $prio) { $failed++ }

$liquid = $survival.GetType("Vintagestory.GameContent.BlockLiquidContainerBase")
$itemStack = $api.GetType("Vintagestory.API.Common.ItemStack")
$containable = if ($liquid -and $itemStack) { $liquid.GetMethod("GetContainableProps", [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static, $null, [Type[]]@($itemStack), $null) } else { $null }
Emit-Check "BlockLiquidContainerBase.GetContainableProps" ($null -ne $containable) ([string]$containable)
if (-not $containable) { $failed++ }

$recipeOpt = $mod.GetType("OptiTime.RecipeLookupCacheOptimization")
$recipePrefix = if ($recipeOpt) { $recipeOpt.GetMethod("Matches_Prefix", $anyStatic) } else { $null }
Emit-Check "Recipe prefix exists" ($null -ne $recipePrefix) ([string]$recipePrefix)
if (-not $recipePrefix) { $failed++ }

$chunkOpt = $mod.GetType("OptiTime.ChunkTesselationOptimization")
$chunkPrefix = if ($chunkOpt) { $chunkOpt.GetMethod("OnBeforeFrame_Prefix", $anyStatic) } else { $null }
$getMul = if ($chunkOpt) { $chunkOpt.GetMethod("GetAdaptiveMultiplier", $anyStatic) } else { $null }
Emit-Check "Chunk prefix exists" ($null -ne $chunkPrefix) ([string]$chunkPrefix)
if (-not $chunkPrefix) { $failed++ }
Emit-Check "Chunk adaptive multiplier exists" ($null -ne $getMul) ([string]$getMul)
if (-not $getMul) { $failed++ }

$handbook = $mod.GetType("OptiTime.HandbookOptimization")
$dirA = if ($handbook) { $handbook.GetMethod("CanItemBeStoredInContainer", $anyStatic) } else { $null }
$dirB = if ($handbook) { $handbook.GetMethod("CanContainerStoreItem", $anyStatic) } else { $null }
Emit-Check "Handbook item->container path exists" ($null -ne $dirA) ([string]$dirA)
if (-not $dirA) { $failed++ }
Emit-Check "Handbook container->item path exists" ($null -ne $dirB) ([string]$dirB)
if (-not $dirB) { $failed++ }

$found = $false
if ($onBefore -and $onBefore.GetMethodBody()) {
    $bytes = $onBefore.GetMethodBody().GetILAsByteArray()
    $hasThree = $bytes -contains 0x19
    $hasTwo = $bytes -contains 0x18
    $hasMul = $bytes -contains 0x5A
    $found = $hasThree -and $hasTwo -and $hasMul
}
Emit-Check "Chunk IL budget anchors" $found "Expected OnBeforeFrame IL to contain ldc.i4.3, ldc.i4.2, and mul opcodes"
if (-not $found) { $failed++ }

if ($failed -gt 0) {
    exit 1
}
