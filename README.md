# OptiTime

Client-side performance optimization mod for [Vintage Story](https://vintagestory.at/).

OptiTime improves frame rates through quality-preserving shader optimizations and [Harmony](https://github.com/pardeike/Harmony) code patches. All optimizations are transparent — visual quality and gameplay behavior are preserved. If any patch conflicts with another mod, it automatically disables itself and falls back to vanilla behavior.

**Requires Vintage Story 1.22.0+** · Client-side only (no server changes needed)

## What it optimizes

### Shader optimizations (quality-preserving)

| Shader | What it does |
|--------|-------------|
| **GodRays** | Early-out when intensity/glow is low; samples reduced (180 → 90) |
| **Volumetric Clouds** | Early alpha clamp to cut overdraw; steps reduced (200 → 128) |
| **SSAO** | Reduced kernel (24/20 → 16/14) plus depth-scaled radius to reduce shimmer |
| **Gaussian Blur** | 17 → 9 taps with paired samples for lower ALU |

### Code optimizations

| Area | What it does |
|------|-------------|
| **Ambient Sounds** | Updates sound positions only when the player moves meaningfully |
| **Background FPS Limiter** | Lowers the frame cap when the window is unfocused |
| **Chunk Tesselation** | Adaptive throttle/boost by real queue size to keep uploads smooth |
| **Dynamic Lights** | Culls lights based on view distance |
| **Entity Animations** | Distance-based LOD for animation updates (conservative thresholds: 48/80 blocks) |
| **Fly Sound** | Volume updates only on meaningful changes |
| **Frame Pacing** | Hybrid sleep/yield/spin frame pacing when VSync is off |
| **GUI Manager** | LINQ-free render iteration with conflict auto-disable |
| **Handbook** | Cached relationships for faster page loading after indexing |
| **Recipe Lookup** | Previous-match reuse, positive-result revalidation, and candidate narrowing |
| **Occlusion Culling** | Dynamic enable gate based on view distance (clamped 70–200 chunks) |
| **Particles** | High-distance particle budget scaling with conservative thresholds |

## Installation

1. Download the latest release from [GitHub Releases](https://github.com/Zaldaryon/OptiTime/releases) or [Mod DB](https://mods.vintagestory.at/optitime).
2. Place the zip in your `VintagestoryData/Mods/` folder.
3. Launch Vintage Story.

All optimizations are enabled by default. No configuration needed.

## Configuration

### In-game commands

Use `.optitime` in chat:

| Command | Description |
|---------|-------------|
| `.optitime` | Show status of all optimizations |
| `.optitime <opt> on\|off` | Toggle a specific optimization |
| `.optitime <opt>` | Show info about an optimization |
| `.optitime profile on\|off\|dump\|reset` | Profiling helpers (off by default) |

Available optimizations: `ambientsound`, `bgfps`, `chunktess`, `dynlights`, `entityanim`, `flysound`, `framepace`, `guimgr`, `handbook`, `occlusion`, `particles`, `recipe`, `shaders`

Commands work in multiplayer without admin privileges.

### ConfigLib GUI (optional)

If [ConfigLib](https://mods.vintagestory.at/configlib) is installed, use `.configlib` to open a GUI for OptiTime settings. ConfigLib is not required.

### Manual configuration (OptiTime.json)

| Setting | Default | Description |
|---------|---------|-------------|
| `ShaderOptimizations` | `true` | Enable shader replacements. Set to `false` for vanilla or other shader mods |
| `RecipeLookupOptimizations` | `true` | Crafting-grid lookup: previous-match fast path, positive cache, candidate narrowing |
| `ParticleViewDistanceScalingEnabled` | `true` | Reduce particle counts at high view distances (75% at 384+, 50% at 512+) |
| `BackgroundFpsLimiterEnabled` | `true` | Lower frame cap when window is unfocused |
| `BackgroundMaxFps` | `20` | Unfocused FPS cap |
| `PreciseFramePacingEnabled` | `true` | Hybrid frame pacing when VSync is off |
| `GuiManagerNoLinqEnabled` | `true` | LINQ-free GUI render iteration |
| `GuiManagerInputNoLinqEnabled` | `false` | No-LINQ input patches (enable only if no conflicting UI mods) |

Changes require a game restart.

## Safety model

- **Startup**: If a Harmony patch fails to apply, that optimization stays disabled. The game starts normally.
- **Runtime**: High-risk patches auto-disable when other mods already patch the same target method.
- **Shader compatibility**: When Ancestral Bliss Shaders is detected, OptiTime disables its shader assets automatically. Code optimizations remain active.

## Compatibility

- Works with most mods.
- Shader mods: toggle `ShaderOptimizations` off if using a different shader pack.
- Harmony conflicts only if another mod patches the exact same methods — OptiTime auto-disables the conflicting patch.
- Client-side only — servers don't need it installed.

## Building from source

Requires the Vintage Story game installation. Set the `VINTAGE_STORY` environment variable to your install path, then:

```bash
# Linux
./build.sh

# Windows
build.bat
```

Output: `bin/OptiTime-<version>.zip`

## Smoke tests

Run the automated smoke test battery from the repo root:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\Invoke-SmokeTests.ps1
```

What it checks:
- Version and packaging consistency
- Build and zip generation
- Critical vanilla reflection contracts for recipe, handbook and chunk paths
- Presence of OptiTime patch entry points
- Vanilla IL pattern required by the chunk upload transpiler
- Recent Vintagestory log scan for OptiTime/Harmony failures

## License

Copyright © 2025 Zaldaryon — All Rights Reserved
