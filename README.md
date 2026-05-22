# OptiTime

Clientside performance optimizations for Vintage Story through shader optimization and code patches.

Current version: 1.5.11

## Performance Gains

**Shader Optimizations (quality-preserving cuts):**
- **Liquid Droplets** — Droplet hash sin → fract (cheaper GPU operation, equivalent visual).
- **Shadow Alpha** — Shadow map discard threshold raised (0.02 → 0.15) for better early-Z.

**Code Optimizations:**
- **Ambient Sounds** - Update sound positions only when the player moves meaningfully.
- **Background FPS Limiter** - Lowers the frame cap when the window is unfocused.
- **Dynamic Lights** - Cull lights based on view distance.
- **Entity Animations** - Distance-based LOD for animation updates (conservative thresholds: 48/80 blocks).
- **Entity Interpolation** - Smoother remote entity movement in multiplayer (flood protection).
- **Fly Sound** - Volume updates only on meaningful changes.
- **Frame Pacing** - Hybrid sleep/yield/spin frame pacing when VSync is off.
- **GUI Manager** - LINQ-free render iteration with conflict auto-disable (opt-out); input patches optional/off by default.
- **Handbook** - Cached relationships for faster page loading after indexing.
- **Recipe Lookup** - Safer crafting-grid lookup with previous-match reuse, positive-result revalidation, and candidate narrowing (default on, opt-out).
- **Occlusion Culling** - Dynamic enable gate based on view distance (clamped 70–200 chunks).
- **Particles** - Probabilistic spawn rejection at high view distances (384+: 25%, 512+: 50% fewer spawns).
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
`ambientsound`, `bgfps`, `dynlights`, `entityanim`, `entityinterp`, `flysound`, `framepace`, `guimgr`, `handbook`, `occlusion`, `particles`, `recipe`, `repulseagents`, `shaders`, `shadowveg`, `tickingblocks`, `weatherwind`

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

## License

Copyright (c) 2025-2026 Zaldaryon - All Rights Reserved
