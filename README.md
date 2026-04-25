# OptiTime

Clientside performance optimizations for Vintage Story through shader optimization and code patches.

## Performance Gains

**Shader Optimizations (quality-preserving cuts):**
- **Liquid Droplets** — Droplet hash sin → fract (cheaper GPU operation, equivalent visual).
- **Shadow Alpha** — Shadow map discard threshold raised (0.02 → 0.15) for better early-Z.

**Code Optimizations:**
- **Ambient Sounds** - Update sound positions only when the player moves meaningfully.
- **Background FPS Limiter** - Lowers the frame cap when the window is unfocused.
- **Chunk Tesselation** - Adaptive throttle/boost by real queue size (normal + priority) to keep uploads smooth.
- **Dynamic Lights** - Cull lights based on view distance.
- **Entity Animations** - Distance-based LOD for animation updates (conservative thresholds: 48/80 blocks).
- **Entity Interpolation** - Smoother remote entity movement in multiplayer (flood protection, interval correction, extrapolation).
- **Fly Sound** - Volume updates only on meaningful changes.
- **Frame Pacing** - Hybrid sleep/yield/spin frame pacing when VSync is off.
- **GUI Manager** - LINQ-free render iteration with conflict auto-disable (opt-out); input patches optional/off by default.
- **Handbook** - Cached relationships for faster page loading after indexing.
- **Recipe Lookup** - Safer crafting-grid lookup with previous-match reuse, positive-result revalidation, and candidate narrowing (default on, opt-out).
- **Occlusion Culling** - Dynamic enable gate based on view distance (clamped 70–200 chunks).
- **Particles** - High-distance particle budget scaling (default on, opt-out) with conservative thresholds.
- **Repulse Agents** - Distance-based cull for entity separation checks (skip non-player entities beyond 64 blocks).
- **Ticking Blocks** - Reuse BlockPos in particle tick loop to eliminate 30K-90K heap allocations/sec.
- **Weather Wind** - Throttle wind speed lookups from every frame to every 4th frame.

**Performance:** Significant FPS improvement with minimal visual impact. Actual gains vary by scene complexity, hardware, and settings.

## Installation

1. Download `OptiTime-X.X.X.zip` from releases
2. Place in `%APPDATA%\VintagestoryData\Mods\` folder
3. Launch Vintage Story

## Configuration

### In-Game Commands

Use `.optitime` command in-game:

```
.optitime                  - Show current status
.optitime status           - Show current status
.optitime <opt> on         - Enable optimization
.optitime <opt> off        - Disable optimization
.optitime <opt>            - Show info about optimization
```

**Available optimizations:**
`ambientsound`, `bgfps`, `chunktess`, `dynlights`, `entityanim`, `entityinterp`, `flysound`, `framepace`, `guimgr`, `handbook`, `occlusion`, `particles`, `recipe`, `repulseagents`, `shaders`, `shadowveg`, `tickingblocks`, `weatherwind`

### ConfigLib GUI (Optional)

OptiTime supports [ConfigLib](https://mods.vintagestory.at/configlib) for in-game GUI configuration:

1. Install ConfigLib mod (optional dependency)
2. Use `.configlib` command to open GUI
3. Navigate to OptiTime settings
4. Changes apply immediately (restart may be required for some settings)

**Note:** ConfigLib is NOT required - OptiTime works perfectly without it using JSON config or commands.

### Manual Configuration (OptiTime.json)

**Advanced config (OptiTime.json):**
- `ShaderOptimizations` - enable OptiTime shader replacements (default on). Set to false to use vanilla or other shader mods. Covers: chunkliquid droplets, shadow alpha threshold.
- `RecipeLookupOptimizations` - enable the crafting-grid lookup optimization bundle: previous-match fast path, exact positive cache with revalidation, and bounded candidate narrowing (default on).
- `ParticleViewDistanceScalingEnabled` - reduce particle counts only at high view distances (default on; 75% at 384+, 50% at 512+).
- `BackgroundFpsLimiterEnabled` - lower the frame cap when the game window is unfocused (default on).
- `BackgroundMaxFps` - unfocused FPS cap used by the background limiter (default 20).
- `PreciseFramePacingEnabled` - replace coarse sleep-based FPS limiting with hybrid pacing when VSync is off (default on).
- `GuiManagerNoLinqEnabled` - keep LINQ-free GUI render iteration (default on).
- `GuiManagerInputNoLinqEnabled` - apply no-LINQ input patches (default off; enable only if you need the tiny allocation savings and no conflicting UI mods).
- `WeatherWindOptimizations` - throttle wind speed lookups from every frame to every 4th frame (default on).
- `TickingBlocksOptimizations` - reuse BlockPos in particle tick loop to eliminate heap allocations (default on).
- `ShadowFarVegetationCullEnabled` - skip vegetation (leaves, grass) in far shadow cascade to reduce shadow rendering cost (default on).

**Examples:**
```
.optitime dynlights on     - Enable dynamic light optimization
.optitime particles off    - Disable particle optimization
.optitime entityanim       - Show entity animation info
.optitime bgfps off        - Disable the unfocused FPS limiter
.optitime framepace on     - Enable precise frame pacing
.optitime recipe off       - Disable recipe lookup optimization
.optitime shaders off      - Disable OptiTime shader replacements
.optitime weatherwind off  - Disable weather wind throttle
.optitime tickingblocks off - Disable ticking blocks GC optimization
.optitime shadowveg off  - Disable far shadow vegetation cull
.optitime entityinterp off - Disable entity interpolation smoothing
.optitime repulseagents off - Disable repulse agents distance cull
```

**Note:** Changes require game restart to take effect.

## Technical Details

- **Method:** Shader replacement (toggle via `ShaderOptimizations`) + Harmony patches
- **Side:** Clientside only (no server changes needed)
- **Compatibility:** Tested with 100 most popular ModDB mods (71 decompiled, 38 with Harmony patches analyzed). Works with all tested mods. Auto-disables conflicting optimizations when detected.
- **Known mod interactions:** **Ancestral Bliss Shaders** — OptiTime disables its shader assets; **Combat Overhaul / Overhaullib** — entity animation optimization auto-disabled; **A Culinary Artillery / Extra Info / Tabletop Games** — handbook optimization auto-disabled; **Electrical Progressive** — handbook optimization auto-disabled
- **Compatibility Behavior:** All Harmony patches check for foreign patches before applying and auto-disable on conflict
- **Visual Impact:** Minimal - optimizations preserve visual quality
- **Default State:** Most optimizations enabled; GUI input patches are off by default
- **Commands:** Work in multiplayer without admin privileges; profiling helpers via `.optitime profile on|off|dump|reset` (off by default, now includes frame pacing and frametime summary data)

## Documentation

See `Documentation/Description/` for detailed information about each optimization.

## Automated Smoke Tests

Run the automated smoke test battery from the repo root:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\Invoke-SmokeTests.ps1
```

Optional parameters:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\Invoke-SmokeTests.ps1 -SkipBuild
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\Invoke-SmokeTests.ps1 -GamePath "C:\Path\To\Vintagestory"
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\Invoke-SmokeTests.ps1 -RequireRecentLog -RecentLogHours 2
```

What it checks automatically:
- Version and packaging consistency
- Build and zip generation
- Critical vanilla reflection contracts for recipe, handbook and chunk paths
- Presence of OptiTime patch entry points
- Vanilla IL pattern required by the chunk upload transpiler
- Recent Vintagestory log scan for OptiTime/Harmony failures

## Version

Current: **1.5.0**

**What's New in 1.5.0:**
- **NEW:** Entity Interpolation optimization — smoother remote entity movement in multiplayer: accelerated playback flood protection (replaces vanilla recursive queue drain), server tick rate interval correction (1/15f → 1/30f), constant velocity extrapolation with 200ms cap and exponential decay correction
- **NEW:** Repulse Agents optimization — distance-based cull for `EntityBehaviorRepulseAgents.OnGameTick`; skips non-player entities beyond 64 blocks (saves spatial partition queries for ~60-70% of creatures)
- **FIX:** Reverted 5 shader optimizations (SSAO, Gaussian Blur, Volumetric Clouds, God Rays, Shadow PCF) that caused visible banding/noise on volumetric clouds. Remaining shaders: Liquid Droplets (hash22) and Shadow Alpha (threshold 0.15)
- **FIX:** Precise frame pacing auto-disabled on Linux — `sched_yield()` causes lag spikes on PREEMPT kernels (confirmed by Red Hat docs, kernel man pages, and benchmarks)
- **FIX:** Handbook harvest indexing now includes FruitingBush and FruitTreeBranch (berry bushes, fruit trees)
- **FIX:** Handbook addStorableInfo/addStoredInInfo falls back to vanilla for complete container coverage (Display, PlantContainer, TroughBase, WearableStats, animalHusbandry, Pie)
- **FIX:** Per-target conflict checks for EntityInterpolation sub-patches (PopQueue transpiler)
- **FIX:** Static Config/staticCapi nulled in Dispose (prevents GC leak across mod reloads)
- **FIX:** ShadowFarVegetationCull now has conflict detection before patching
- **IMPROVED:** Source reorganized into domain subfolders (Audio/, Core/, Entity/, Gui/, Systems/)
- **IMPROVED:** HandbookOptimization split into 3 partial class files for maintainability
- **IMPROVED:** ParticleOptimization migrated from `dynamic` dispatch to cached FieldInfo
- **IMPROVED:** All documentation renamed to kebab-case
- **IMPROVED:** Compatibility verified with 100 most popular ModDB mods (71 decompiled, 38 with Harmony patches analyzed)

**Previous Release (1.4.11):**
- **FIX:** Liquid Droplets shader — replaced broken fract-only hash with Dave Hoskins hash22 (industry-standard sin-free hash); droplets now animate with proper randomness instead of all syncing together
- **NEW:** All optimizations now exposed in ConfigLib GUI — WeatherWind, TickingBlocks, ShadowVegetationCull, BackgroundMaxFps, and MouseMoveCoalescing added to the settings panel
- **NEW:** Complete translations for all new ConfigLib settings across all 31 languages

**Previous Release (1.4.10):**
- **FIX:** Dynamic lights intensity — restored `lighthsv[2]` multiplier lost in 1.4.8 FieldRefAccess refactor; held light sources (lanterns, torches) now emit light at correct intensity
- **NEW:** Shadow PCF reduction — far cascade shadow sampling reduced from 9-tap 3×3 grid to 4-tap Vogel disk (56% fewer texture lookups, near cascade unchanged)
- **NEW:** Shadow alpha threshold — shadow map fragment discard threshold raised from 0.02 to 0.15 (better early-Z, minimal visual change)
- **NEW:** Shadow vegetation cull — skip vegetation (BlendNoCull pass) in far shadow cascade; leaf/grass shadows at 150+ blocks are invisible anyway (config: `ShadowFarVegetationCullEnabled`, command: `.optitime shadowveg on/off`)
- **IMPROVED:** All log prefixes standardized to `[OptiTime]`

**Previous Release (1.4.9):**
- **FIX:** TranslationService thread-safety crash — vanilla `HasTranslation` writes to a plain `HashSet` from background threads during `CreativeTab.CreateSearchCache`, causing `InvalidOperationException` on some mod configurations. Transpiler replaces `HashSet.Add` with `ConcurrentDictionary.TryAdd` (always-on, no config toggle)

**Previous Release (1.4.8):**
- **NEW:** Liquid Droplets shader — droplet hash sin → fract (cheaper GPU operation, equivalent visual)
- **NEW:** Weather Wind optimization — throttle wind speed lookups from every frame to every 4th frame (config: `WeatherWindOptimizations`)
- **NEW:** Ticking Blocks optimization — reuse BlockPos in particle tick loop to eliminate 30K-90K heap allocations/sec (config: `TickingBlocksOptimizations`)
- **IMPROVED:** Chunk Tesselation reworked — removed reflection-heavy prefix, now uses transpiler-only approach (near-zero overhead)
- **IMPROVED:** Replaced `dynamic` dispatch with `FieldRefAccess` in AmbientSound, DynamicLights, EntityAnimation, FlySound (eliminates DLR overhead)
- **REMOVED:** `godrays.vsh` (was byte-identical to vanilla, zero benefit)
- **REMOVED:** `MeshPoolOptimization.cs` (dormant code, never registered)

**Previous Release (1.4.7):**
- **TARGET:** Vintage Story 1.22.0+ / .NET 10
- **FIX:** Recipe Lookup updated for 1.22.0 API (new Matches/MatchesShapeLess/MatchesAtPosition signatures, public ResolvedIngredients property)
- **FIX:** Handbook updated for 1.22.0 API (anvils parameter, addEatenByInfo, addProcessorForInfo sections)
- **FIX:** Volumetric Clouds shader rewritten for 1.22.0 depth-binned OIT system (6 render targets, liquidDepth support)
- **FIX:** Entity.ServerPos → Entity.Pos, ResolvedItemstack → ResolvedItemStack, IsWildCard → MatchingType
- **IMPROVED:** System.Threading.Lock replaces object locks (zero-alloc lock scope, no sync block inflation)
- **IMPROVED:** .NET 10 automatic gains: FrozenDictionary 58% faster, ConcurrentDictionary enumeration 84% faster, JIT devirtualization, try/finally inlining
- **NEW:** Linux build.sh script (modeled after ZenGameplay)

**Previous Release (1.4.6):**
- **NEW:** `RecipeLookupOptimizations` is now exposed everywhere consistently
  - JSON config toggle in `OptiTime.json`
  - ConfigLib toggle in the optional GUI
  - `.optitime recipe on|off` in-game command
- **IMPROVED:** Recipe lookup is now safer and more effective for large packs
  - Previous successful crafting recipe is revalidated first
  - Exact positive crafting-grid states are cached and revalidated before reuse
  - Candidate recipes are narrowed before full matching while preserving vanilla order
  - Removed the old persistent negative-result cache
- **IMPROVED:** Recipe lookup now auto-disables fully if another mod already patches the same recipe targets
- **NEW:** `.optitime shaders on|off` command parity for the existing shader config
- **FIX:** `Invoke-SmokeTests.ps1` no longer fails on single `Select-String` matches

**Previous Release (1.4.1):**
- **NEW:** ConfigLib integration - Optional GUI for settings (use `.configlib` command)
- **NEW:** Complete translations for all 31 vanilla languages
- **NEW:** All user-facing text moved to language files
- **IMPROVED:** Entity Animation early distance culling (5-15% additional savings)
- **IMPROVED:** Handbook sorting performance (allStacks order cached once during indexing)
- **REMOVED:** Block Scanning optimization (ensured vanilla-identical behavior for ticking blocks)
- **DOCUMENTATION:** Added comprehensive behavioral analysis document

**Previous Release (1.3.12):**
- **COMPATIBILITY FIX:** Added explicit parameter types to all Harmony patches
  - Fixes `AmbiguousMatchException` conflicts with other mods
  - Better compatibility when multiple mods patch the same methods
- **THREAD SAFETY:** Chunk Tesselation optimization now uses locks for thread-safe queue access
  - Prevents race conditions with mods doing custom BlockEntity tesselation
  - Safer for mods that generate mesh data in the tesselation thread
- **ANCESTRAL BLISS COMPATIBILITY:** Auto-detects shader mod and clarifies shader priority
  - OptiTime disables its shader assets so Ancestral Bliss shaders are used
  - Code optimizations remain active

**Previous Release (1.3.10):**
- GUI render optimization auto-disables if another mod patches GUI render; input patches now optional (default off).
- Shader tweaks: godrays early-outs + sample reduction, SSAO kernel reduction + depth‑scaled radius, cloud alpha clamp + step reduction, paired blur taps.
- Particle scaling: opt-out, only trims counts at high view distances.
- Chunk tesselation: adaptive multiplier uses real normal+priority queues.

**Previous Release (1.3.8):**
- **FIX:** Fixed crash when clicking items in chests with Toolsmith mod installed
  - Added defensive null checks to GUI slot rendering
  - Prevents NullReferenceException in ComposeSlotOverlays
  - Improves compatibility with mods that patch GUI methods
  - No performance impact - all optimizations remain enabled

**Previous Release (1.3.7):**

- **CRITICAL FIX:** Improved Chunk Tesselation optimization for berry bushes and block entities
  - Multi-tier adaptive throttling (1x/2x/3x/4x) based on queue size
  - Berry bushes now get 4x BOOST (50-150 chunk queue) instead of throttling
  - Raised throttle threshold from 50 to 150+ to avoid catching normal gameplay
  - Emergency throttle (1x) only kicks in at 300+ chunks (extreme loads)
  - Added diagnostic logging for queue monitoring
  - Fixes FPS drops when looking at berry bushes, cattails, tree leaves, and block entities
  - Also benefits: All blocks with wind motion, climate/seasonal color mapping

**Previous Release (1.3.6):**
- Fixed inverted logic in Chunk Tesselation (incomplete fix, superseded by 1.3.7)

**Previous Release (1.3.4):**
- **FIX:** Player Environment Tracker optimization no longer interferes with underwater fog
  - Changed from prefix to postfix patch to run after vanilla code
  - Optimization now only caches light levels, doesn't touch water/lava properties
  - Underwater fog rendering works exactly as vanilla

**Previous Release (1.2.2):**
- **CRITICAL FIX:** Chunk Tesselation now uses adaptive throttling to prevent memory leaks
  - Automatically adjusts based on chunk queue size
  - Prevents OutOfMemoryException crashes during mass terraforming/digging
  - Fixes MeshRef memory leak warnings
  - Maintains smooth frame pacing during normal gameplay
- Fixed obfuscation issues with chunk tesselation optimization

**Previous Release (1.2.1):**
- Added Entity Name Tag distance culling optimization
- Fixed ambient sound position update allocations
- Disabled problematic optimizations (guimgr, soundengine) by default
- Comprehensive performance analysis and documentation

## License

Copyright (c) 2025-2026 Zaldaryon - All Rights Reserved
