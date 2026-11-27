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

$searchDirs = @(
    $GamePath,
    (Join-Path $GamePath "Lib"),
    (Join-Path $GamePath "Mods")
)
$resolveHandler = [System.ResolveEventHandler]{
    param($sender, $eventArgs)
    $assemblyFile = (New-Object System.Reflection.AssemblyName($eventArgs.Name)).Name + ".dll"
    foreach ($dir in $searchDirs) {
        $candidate = Join-Path $dir $assemblyFile
        if (Test-Path -LiteralPath $candidate) {
            return [System.Reflection.Assembly]::LoadFrom($candidate)
        }
    }
    return $null
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($resolveHandler)

$api = [System.Reflection.Assembly]::LoadFrom((Join-Path $GamePath "VintagestoryAPI.dll"))
$lib = [System.Reflection.Assembly]::LoadFrom((Join-Path $GamePath "VintagestoryLib.dll"))
$survival = [System.Reflection.Assembly]::LoadFrom((Join-Path (Join-Path $GamePath "Mods") "VSSurvivalMod.dll"))
$mod = [System.Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $DllPath).Path)

$publicInstance = [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Instance
$nonPublicInstance = [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Instance
$anyStatic = [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static

$failed = 0

$grid = $api.GetType("Vintagestory.API.Common.GridRecipe")
$player = $api.GetType("Vintagestory.API.Common.IPlayer")
$world = $api.GetType("Vintagestory.API.Common.IWorldAccessor")
$slot = $api.GetType("Vintagestory.API.Common.ItemSlot")
$slots = if ($slot) { $slot.MakeArrayType() } else { $null }
$recipeIngredient = $api.GetType("Vintagestory.API.Common.IRecipeIngredient")
$recipeIngredients = if ($recipeIngredient) { $recipeIngredient.MakeArrayType() } else { $null }

$gridMatches = if ($grid -and $player -and $world -and $slots) { $grid.GetMethod("Matches", $publicInstance, $null, [Type[]]@($player, $world, $slots, [int]), $null) } else { $null }
$gridShapeLess = if ($grid -and $slots -and $world -and $recipeIngredients) { $grid.GetMethod("MatchesShapeLess", $nonPublicInstance, $null, [Type[]]@($slots, $world, $recipeIngredients), $null) } else { $null }
Emit-Check "GridRecipe.Matches" ($null -ne $gridMatches) ([string]$gridMatches)
if (-not $gridMatches) { $failed++ }
Emit-Check "GridRecipe.MatchesShapeLess" ($null -ne $gridShapeLess) ([string]$gridShapeLess)
if (-not $gridShapeLess) { $failed++ }

$liquid = $survival.GetType("Vintagestory.GameContent.BlockLiquidContainerBase")
$itemStack = $api.GetType("Vintagestory.API.Common.ItemStack")
$containable = if ($liquid -and $itemStack) { $liquid.GetMethod("GetContainableProps", [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static, $null, [Type[]]@($itemStack), $null) } else { $null }
Emit-Check "BlockLiquidContainerBase.GetContainableProps" ($null -ne $containable) ([string]$containable)
if (-not $containable) { $failed++ }

$recipeOpt = $mod.GetType("OptiTime.RecipeLookupCacheOptimization")
$recipePrefix = if ($recipeOpt) { $recipeOpt.GetMethod("MatchesWithWorld_Prefix", $anyStatic) } else { $null }
Emit-Check "Recipe prefix exists" ($null -ne $recipePrefix) ([string]$recipePrefix)
if (-not $recipePrefix) { $failed++ }

$handbook = $mod.GetType("OptiTime.HandbookOptimization")
$dirA = if ($handbook) { $handbook.GetMethod("CanItemBeStoredInContainer", $anyStatic) } else { $null }
$dirB = if ($handbook) { $handbook.GetMethod("CanContainerStoreItem", $anyStatic) } else { $null }
Emit-Check "Handbook item->container path exists" ($null -ne $dirA) ([string]$dirA)
if (-not $dirA) { $failed++ }
Emit-Check "Handbook container->item path exists" ($null -ne $dirB) ([string]$dirB)
if (-not $dirB) { $failed++ }

$chunkTessMgr = $lib.GetType("Vintagestory.Client.NoObf.ChunkTesselatorManager")
$onBefore = if ($chunkTessMgr) { $chunkTessMgr.GetMethod("OnBeforeFrame", $publicInstance, $null, [Type[]]@([float]), $null) } else { $null }
Emit-Check "ChunkTesselatorManager.OnBeforeFrame" ($null -ne $onBefore) ([string]$onBefore)
if (-not $onBefore) { $failed++ }

$chunkUploadOpt = $mod.GetType("OptiTime.ChunkUploadOptimization")
$chunkUploadPrefix = if ($chunkUploadOpt) { $chunkUploadOpt.GetMethod("OnBeforeFrame_Prefix", $anyStatic) } else { $null }
Emit-Check "ChunkUploadOptimization prefix exists" ($null -ne $chunkUploadPrefix) ([string]$chunkUploadPrefix)
if (-not $chunkUploadPrefix) { $failed++ }

if ($failed -gt 0) {
    exit 1
}
