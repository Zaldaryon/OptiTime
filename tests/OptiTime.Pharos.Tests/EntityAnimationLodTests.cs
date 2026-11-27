using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Zaldaryon.Pharos.Player;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Measures what the entity animation level of detail does to the pose work of
/// real entities in a running world.
///
/// <para>The pose work is <c>ClientAnimator.calculateMatrices</c>, the step that
/// walks the shape's joint hierarchy every frame. The LOD exists to run it less
/// often for distant creatures while the animation clock, triggers and sounds
/// keep ticking every frame. Counting the calls per animator is the only
/// observation that can tell a working LOD from one whose diagnostics count
/// skips that never happen.</para>
///
/// <para>Near and far creatures are spawned along the camera's line of sight so
/// both are drawn, and the near one is the frame reference: it is inside the
/// close range and must be posed on every frame it is drawn.</para>
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class EntityAnimationLodTests : OptiTimeScenarioBase
{
    /// <summary>Well inside the close range at every view distance.</summary>
    private const double NearDistance = 20;

    /// <summary>
    /// Past the close range plus its hysteresis margin at the suite's view
    /// distance of 256 (close range 64 blocks, entered at 70.4).
    /// </summary>
    private const double FarDistance = 96;

    private const int MeasuredFrames = 240;

    private static readonly ConcurrentDictionary<object, StrongBox<int>> PoseCounts =
        new(ReferenceEqualityComparer.Instance);

    private readonly record struct Probes(long Near, long Far, long Bobber, Vec3d NearAt, Vec3d FarAt);

    private readonly record struct Measurement(int Near, int Far, int Bobber, bool AllRendered);

    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task DistantCreaturesArePosedLessOftenWhileNearOnesAndProjectilesKeepFullRate()
    {
        // Removed again at the end so no later scenario sees a foreign patch on game code.
        Harmony poseCounter = InstallPoseCounter();
        try
        {
            Probes probes = await SpawnProbesAsync();
            await WithOptimizationStateRestoredAsync("entityanim", () => AssertPoseRatesAsync(probes));
        }
        finally
        {
            poseCounter.UnpatchAll(poseCounter.Id);
        }
    }

    private async Task AssertPoseRatesAsync(Probes probes)
    {
        await SetOptimizationAsync("entityanim", enabled: true);
        Measurement lod = await CountPosesAsync(probes);
        Console.WriteLine($"entityanim on:  near={lod.Near} far={lod.Far} bobber={lod.Bobber} over {MeasuredFrames} frames");

        await SetOptimizationAsync("entityanim", enabled: false);
        Measurement vanilla = await CountPosesAsync(probes);
        Console.WriteLine($"entityanim off: near={vanilla.Near} far={vanilla.Far} bobber={vanilla.Bobber} over {MeasuredFrames} frames");

        Assert.True(lod.AllRendered && vanilla.AllRendered,
            "A probe entity was not drawn during the measurement, so its pose count says nothing about the LOD.");
        Assert.True(lod.Near >= MeasuredFrames * 0.8,
            $"The near creature was posed on {lod.Near} of {MeasuredFrames} frames. Inside the close range it must be posed every frame.");

        Assert.True(lod.Far <= lod.Near * 0.65,
            $"The far creature was posed {lod.Far} times against {lod.Near} for the near one. " +
            "With the LOD on, a creature past the close range must be posed at most every second frame.");
        Assert.True(lod.Far >= lod.Near * 0.25,
            $"The far creature was posed only {lod.Far} times against {lod.Near}. It must still be posed every second or third frame.");

        Assert.True(lod.Bobber >= lod.Near * 0.9,
            $"The far fishing bobber was posed {lod.Bobber} times against {lod.Near}. Projectiles must never be throttled.");

        Assert.True(vanilla.Far >= vanilla.Near * 0.9,
            $"With entityanim off the far creature was posed {vanilla.Far} times against {vanilla.Near}. " +
            "Turning the option off must restore the vanilla every-frame pose.");
    }

    /// <summary>
    /// Reports the CPU time of the entity passes (OnBeforeRender and the passes that walk the
    /// renderers) with a herd of distant animals in view, option on against option off, in
    /// alternating rounds. Wall-clock numbers on a shared machine are reported, not asserted.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 1_800_000)]
    [Trait("Category", "Benchmark")]
    public async Task EntityPassCostWithADistantHerd()
    {
        // OptiTime treats foreign prefixes on these methods as conflicts at boot, so the timer is
        // removed again before any later scenario boots a client.
        Harmony timer = EntityPassTimer.Install();
        try
        {
            await MeasureEntityPassCostAsync();
        }
        finally
        {
            timer.UnpatchAll(timer.Id);
        }
    }

    private async Task MeasureEntityPassCostAsync()
    {
        (CameraPose pose, double dx, double dz) = await FaceTheHorizonAsync();
        ServerHost!.RunOnGameThread(() =>
        {
            ICoreServerAPI api = (ICoreServerAPI)ServerHost.Server.Api;
            for (int row = 0; row < 8; row++)
            for (int column = 0; column < 8; column++)
            {
                api.World.SpawnEntity(CreateOnPlatform(api, "game:chicken-hen", pose, dx, dz, 90 + row * 6, (column - 3.5) * 6));
            }
            return true;
        });
        await Session!.StepFramesAsync(300);

        List<string> rows =
        [
            "| Round | entityanim | Frames | OnBeforeRender ms/frame | Entity draw passes ms/frame | Name tag HUD ms/frame | Median frame ms | Render thread B/frame |",
            "| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |",
        ];
        await WithOptimizationStateRestoredAsync("entityanim", async () =>
        {
            for (int round = 1; round <= 5; round++)
            {
                // Alternate the order so neither state always runs second.
                foreach (bool enabled in round % 2 == 1 ? new[] { false, true } : new[] { true, false })
                {
                    await SetOptimizationAsync("entityanim", enabled);
                    await Session.StepFramesAsync(60);
                    EntityPassTimer.Reset();
                    Sample sample = await MeasureAsync($"entityanim-{(enabled ? "on" : "off")}-{round}", MeasuredFrames);
                    (int frames, double beforeMs, double passesMs, double nameTagsMs) = EntityPassTimer.Snapshot();
                    int perFrame = Math.Max(1, frames);
                    rows.Add($"| {round} | {(enabled ? "on" : "off")} | {frames} | {beforeMs / perFrame:F4} | {passesMs / perFrame:F4} | {nameTagsMs / perFrame:F4} | {sample.Median.TotalMilliseconds:F3} | {sample.AllocatedBytesPerFrame:F0} |");
                }
            }
        });

        string report = string.Join('\n', rows);
        Console.WriteLine(report);
        await WriteArtifactAsync(nameof(EntityAnimationLodTests), "entity-pass-cost.md", report);
    }

    private async Task<(CameraPose Pose, double Dx, double Dz)> FaceTheHorizonAsync()
    {
        await WarmUpAsync();
        CameraPose pose = await ApplyPoseAsync(OptiTimeConfig.Poses[1]);
        double dx = pose.Target[0] - pose.Eye[0];
        double dz = pose.Target[2] - pose.Eye[2];
        double length = Math.Sqrt(dx * dx + dz * dz);
        return (pose, dx / length, dz / length);
    }

    /// <summary>
    /// Creates an entity on a 7x7 stone platform four blocks below the camera, along its line of
    /// sight. The platform keeps every probe at a known height in view whatever the terrain under
    /// it does; standing on the terrain, a creature can walk off a slope out of the frustum or
    /// fall into water before it is measured.
    /// </summary>
    private static Entity CreateOnPlatform(ICoreServerAPI api, string code, CameraPose pose, double dx, double dz, double distance, double sideways)
    {
        Vec3d at = PlatformTop(pose, dx, dz, distance, sideways);
        int stone = api.World.GetBlock(new AssetLocation("game:rock-granite"))?.BlockId
            ?? throw new InvalidOperationException("game:rock-granite is not registered.");
        BlockPos floor = new((int)Math.Floor(at.X), (int)at.Y - 1, (int)Math.Floor(at.Z));
        for (int offsetX = -3; offsetX <= 3; offsetX++)
        for (int offsetZ = -3; offsetZ <= 3; offsetZ++)
        {
            api.World.BlockAccessor.SetBlock(stone, floor.AddCopy(offsetX, 0, offsetZ));
        }

        EntityProperties type = api.World.GetEntityType(new AssetLocation(code))
            ?? throw new InvalidOperationException($"Entity type {code} is not registered.");
        Entity entity = api.World.ClassRegistry.CreateEntity(type);
        entity.Pos.SetPos(at);
        return entity;
    }

    private static Vec3d PlatformTop(CameraPose pose, double dx, double dz, double distance, double sideways) => new(
        Math.Floor(pose.Eye[0] + dx * distance - dz * sideways) + 0.5,
        Math.Floor(pose.Eye[1]) - 3,
        Math.Floor(pose.Eye[2] + dz * distance + dx * sideways) + 0.5);

    private async Task<Probes> SpawnProbesAsync()
    {
        (CameraPose pose, double dx, double dz) = await FaceTheHorizonAsync();
        long observerId = ((ClientTestPlayer)Player!).RawEntity!.EntityId;

        Probes probes = ServerHost!.RunOnGameThread(() =>
        {
            ICoreServerAPI api = (ICoreServerAPI)ServerHost.Server.Api;
            Entity near = CreateOnPlatform(api, "game:chicken-hen", pose, dx, dz, NearDistance, 0);
            Entity far = CreateOnPlatform(api, "game:chicken-hen", pose, dx, dz, FarDistance, 0);
            Entity bobber = CreateOnPlatform(api, "game:bobber-normal", pose, dx, dz, FarDistance, 3);

            // A bobber removes itself when the fisher it is tied to is gone.
            AccessTools.Property(bobber.GetType(), "AttachedToEntityId").SetValue(bobber, observerId);

            api.World.SpawnEntity(near);
            api.World.SpawnEntity(far);
            api.World.SpawnEntity(bobber);
            return new Probes(near.EntityId, far.EntityId, bobber.EntityId,
                PlatformTop(pose, dx, dz, NearDistance, 0), PlatformTop(pose, dx, dz, FarDistance, 0));
        });

        bool arrived = await StepUntilAsync(() => Client!.RunOnClientThread(() =>
        {
            var world = Client.Client.World;
            return world.GetEntityById(probes.Near)?.IsRendered == true
                && world.GetEntityById(probes.Far)?.IsRendered == true
                && world.GetEntityById(probes.Bobber)?.IsRendered == true;
        }), maxFrames: 900);
        Assert.True(arrived, "The probe entities never became visible on the client: " + Client!.RunOnClientThread(() =>
            string.Join("; ", new[] { ("near", probes.Near), ("far", probes.Far), ("bobber", probes.Bobber) }.Select(probe =>
            {
                Entity? entity = Client.Client.World.GetEntityById(probe.Item2);
                return entity is null
                    ? $"{probe.Item1} missing"
                    : $"{probe.Item1} at {entity.Pos.XYZ} rendered={entity.IsRendered} alive={entity.Alive}";
            }))));

        await Session!.StepFramesAsync(60);
        return probes;
    }

    private async Task<Measurement> CountPosesAsync(Probes probes)
    {
        // Put the creatures back in the middle of their platforms, then let a toggle and the
        // LOD's hysteresis settle before counting.
        ServerHost!.RunOnGameThread(() =>
        {
            ICoreServerAPI api = (ICoreServerAPI)ServerHost.Server.Api;
            api.World.GetEntityById(probes.Near)?.TeleportToDouble(probes.NearAt.X, probes.NearAt.Y, probes.NearAt.Z);
            api.World.GetEntityById(probes.Far)?.TeleportToDouble(probes.FarAt.X, probes.FarAt.Y, probes.FarAt.Z);
            return true;
        });
        await Session!.StepFramesAsync(30);

        StrongBox<int>[] counters = Client!.RunOnClientThread(() =>
        {
            PoseCounts.Clear();
            var world = Client.Client.World;
            return new[] { probes.Near, probes.Far, probes.Bobber }
                .Select(id =>
                {
                    object animator = world.GetEntityById(id)?.AnimManager?.Animator
                        ?? throw new InvalidOperationException($"Probe entity {id} has no animator on the client.");
                    return PoseCounts.GetOrAdd(animator, _ => new StrongBox<int>());
                })
                .ToArray();
        });

        bool allRendered = true;
        for (int frame = 0; frame < MeasuredFrames; frame += 20)
        {
            await Session.StepFramesAsync(20);
            allRendered &= Client.RunOnClientThread(() =>
            {
                var world = Client.Client.World;
                return world.GetEntityById(probes.Near)?.IsRendered == true
                    && world.GetEntityById(probes.Far)?.IsRendered == true
                    && world.GetEntityById(probes.Bobber)?.IsRendered == true;
            });
        }

        return Client.RunOnClientThread(() =>
        {
            Measurement measurement = new(
                Volatile.Read(ref counters[0].Value),
                Volatile.Read(ref counters[1].Value),
                Volatile.Read(ref counters[2].Value),
                allRendered);
            PoseCounts.Clear();
            return measurement;
        });
    }

    private static Harmony InstallPoseCounter()
    {
        var target = AccessTools.Method(typeof(ClientAnimator), "calculateMatrices", [typeof(float)])
            ?? throw new InvalidOperationException("ClientAnimator.calculateMatrices(float) not found.");

        Harmony harmony = new("optitime.pharos.entityanim-lod");
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(EntityAnimationLodTests), nameof(CountPose)));
        return harmony;
    }

    private static void CountPose(ClientAnimator __instance)
    {
        if (__instance.CalculateMatrices && PoseCounts.TryGetValue(__instance, out StrongBox<int>? counter))
        {
            Interlocked.Increment(ref counter.Value);
        }
    }

    /// <summary>
    /// Times SystemRenderEntities.OnBeforeRender and the entity passes that walk the renderers,
    /// including every prefix on them, on the client render thread.
    /// </summary>
    private static class EntityPassTimer
    {
        private static readonly string[] Passes =
        [
            "Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderOpaque3D",
            "Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderAfterOIT",
            "Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderFrame2D",
        ];

        private static int frames;
        private static long beforeTicks;
        private static long passTicks;
        private static long nameTagTicks;


        public static void Reset()
        {
            Volatile.Write(ref frames, 0);
            Volatile.Write(ref beforeTicks, 0);
            Volatile.Write(ref passTicks, 0);
            Volatile.Write(ref nameTagTicks, 0);
        }

        public static (int Frames, double BeforeMs, double PassesMs, double NameTagsMs) Snapshot() =>
            (Volatile.Read(ref frames),
             Volatile.Read(ref beforeTicks) * 1000.0 / Stopwatch.Frequency,
             Volatile.Read(ref passTicks) * 1000.0 / Stopwatch.Frequency,
             Volatile.Read(ref nameTagTicks) * 1000.0 / Stopwatch.Frequency);

        public static Harmony Install()
        {
            Harmony harmony = new("optitime.pharos.entityanim-timer");
            HarmonyMethod start = new(typeof(EntityPassTimer), nameof(Start)) { priority = Priority.First };

            var before = AccessTools.Method("Vintagestory.Client.NoObf.SystemRenderEntities:OnBeforeRender", [typeof(float)])
                ?? throw new InvalidOperationException("SystemRenderEntities.OnBeforeRender not found.");
            harmony.Patch(before, prefix: start,
                postfix: new HarmonyMethod(typeof(EntityPassTimer), nameof(StopBefore)) { priority = Priority.Last });

            HarmonyMethod stopPass = new(typeof(EntityPassTimer), nameof(StopPass)) { priority = Priority.Last };
            foreach (string pass in Passes)
            {
                var method = AccessTools.Method(pass, [typeof(float)])
                    ?? throw new InvalidOperationException(pass + " not found.");
                harmony.Patch(method, prefix: start, postfix: stopPass);
            }

            var nameTags = AccessTools.Method("Vintagestory.Client.NoObf.HudEntityNameTags:OnRenderGUI", [typeof(float)])
                ?? throw new InvalidOperationException("HudEntityNameTags.OnRenderGUI not found.");
            harmony.Patch(nameTags, prefix: start,
                postfix: new HarmonyMethod(typeof(EntityPassTimer), nameof(StopNameTags)) { priority = Priority.Last });

            return harmony;
        }

        private static void Start(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void StopBefore(long __state)
        {
            Interlocked.Add(ref beforeTicks, Stopwatch.GetTimestamp() - __state);
            Interlocked.Increment(ref frames);
        }

        private static void StopPass(long __state) =>
            Interlocked.Add(ref passTicks, Stopwatch.GetTimestamp() - __state);

        private static void StopNameTags(long __state) =>
            Interlocked.Add(ref nameTagTicks, Stopwatch.GetTimestamp() - __state);
    }
}
