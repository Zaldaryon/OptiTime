namespace OptiTime.Pharos.Tests;

/// <summary>
/// The counter names each diag module writes, taken verbatim from the
/// <c>Dump</c> implementation in <c>Core/Diagnostics/Module*.cs</c>.
///
/// They live here rather than in the tests so that a counter the mod renames
/// breaks one file with one clear message, instead of twenty call sites that
/// each compare a defaulted zero against a threshold.
/// </summary>
public static class DiagCounter
{
    // ModuleParticles: "requested=.. allowed=.. (..%) culled byVD=.. byFrustum=..
    //                   byOccupancy=.. byBudget=.. elapsedS=.."
    public const string ParticlesRequested = "requested";
    public const string ParticlesAllowed = "allowed";
    public const string ParticlesCulledByViewDistance = "byVD";
    public const string ParticlesCulledByFrustum = "byFrustum";
    public const string ParticlesCulledByOccupancy = "byOccupancy";
    public const string ParticlesCulledByBudget = "byBudget";
    public const string ElapsedSeconds = "elapsedS";

    // ModuleAmbientSound: "calls=.. skipped=.. (..%) elapsedS=.."
    public const string Calls = "calls";
    public const string Skipped = "skipped";

    // ModuleFlySound: "calls=.. suppressed=.. (..%) elapsedS=.."
    public const string Suppressed = "suppressed";

    // ModuleWeatherWind: "ticks=.. skipped=.. (..%) elapsedS=.."
    public const string Ticks = "ticks";

    // ModuleRepulse: "ticks=.. skipped=.. (..%) ranVanilla=.. elapsedS=.."
    public const string RanVanilla = "ranVanilla";

    // ModuleShadowVeg: "shadowPasses=.. vegCulled=.. (..%) elapsedS=.."
    public const string ShadowPasses = "shadowPasses";
    public const string VegCulled = "vegCulled";

    // ModuleShadowEntity: "entities=.. culled=.. (..%) shadowQuality=.. cascadeRange=.. cullDist=80 elapsedS=.."
    public const string Entities = "entities";
    public const string Culled = "culled";
    public const string ShadowQuality = "shadowQuality";
    public const string CascadeRange = "cascadeRange";

    // ModuleEntityAnim: "entities=.. full=.. lodMedium=.. lodFar=.. skipped=..% elapsedS=.."
    public const string Full = "full";
    public const string LodMedium = "lodMedium";
    public const string LodFar = "lodFar";

    // ModuleDynLights: "frames=.. entitiesLit=.. avgPerFrame=.. overflow=.. elapsedS=.."
    public const string Frames = "frames";
    public const string EntitiesLit = "entitiesLit";
    public const string Overflow = "overflow";

    // ModuleGuiMgr: "renderCalls=.. noLinqPath=.. (..%) elapsedS=.."
    public const string RenderCalls = "renderCalls";
    public const string NoLinqPath = "noLinqPath";

    // ModuleHandbook: "indexer=.. cacheLookups=.. hits=.. (..%) misses=.. elapsedS=.."
    public const string Indexer = "indexer";
    public const string CacheLookups = "cacheLookups";
    public const string Hits = "hits";
    public const string Misses = "misses";

    // ModuleRecipe: "lookups=.. hits=.. (..%) misses=.. invalidations=.. elapsedS=.."
    public const string Lookups = "lookups";
    public const string Invalidations = "invalidations";

    // ModuleOcclusion: "framesWithDynamicThreshold=.. elapsedS=.. rate=../s"
    public const string FramesWithDynamicThreshold = "framesWithDynamicThreshold";

    // ModuleTickingBlocks: "allocsAvoided=.. gcBytesEst=..MB elapsedS=.. rate=../s"
    public const string AllocsAvoided = "allocsAvoided";
    public const string GcBytesEstimateMb = "gcBytesEst";

    // ModuleFramePace: "precise=.. fallback=.. sleepMs=.. avgOvershoot=..
    //                      p99Overshoot=.. maxOvershoot=.."
    public const string Precise = "precise";
    public const string Fallback = "fallback";
    public const string SleepMs = "sleepMs";
    public const string AverageOvershootMs = "avgOvershoot";
    public const string P99OvershootMs = "p99Overshoot";
    public const string MaxOvershootMs = "maxOvershoot";

    // ModuleBgFps: "focused=.. unfocused=.. unfocusedFps=.. elapsedS=.."
    public const string Focused = "focused";
    public const string Unfocused = "unfocused";
    public const string UnfocusedFps = "unfocusedFps";

    // EntityInterpolationOptimization.DumpDiagnostic: counters prefixed "interp".
    public const string InterpPackets = "packets";
    public const string InterpExtrapolated = "extrapolated";
}