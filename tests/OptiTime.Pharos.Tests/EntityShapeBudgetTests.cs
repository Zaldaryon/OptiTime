using HarmonyLib;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Checks that reshapes of entities that already have a mesh are spread over frames.
///
/// <para>When an entity's shape changes (outfit, gear, attachments), the next
/// <c>EntityShapeRenderer.BeforeRender</c> calls <c>TesselateShape</c>, which runs the
/// shape cloning, gear step-parenting and texture lookups on the render thread. A dressed
/// villager costs roughly 10 to 25 ms there, so a group of them changing together stalls
/// one frame for over 100 ms. The flag a reshape clears is <c>Entity.ShapeFresh</c>, set
/// again inside <c>OnTesselation</c>, so watching it flip back counts real reshapes without
/// patching game code.</para>
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class EntityShapeBudgetTests : OptiTimeScenarioBase
{
    private const int Villagers = 8;

    private readonly record struct FrameState(int Fresh, bool AllMeshedAndDrawn);

    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task ReshapesOfMeshedVillagersAreSpreadOverFramesWithoutInvisibleFrames()
    {
        long[] villagers = await SpawnVillagersAsync();

        // The default configuration first, so the run also shows the defect on a build without the option.
        List<FrameState> spread = await ReshapeAllAsync(villagers);
        Console.WriteLine("defaults, fresh per frame: " + string.Join(", ", spread.Select(s => s.Fresh)));
        for (int frame = 0; frame < spread.Count; frame++)
        {
            int previous = frame == 0 ? 0 : spread[frame - 1].Fresh;
            Assert.True(spread[frame].Fresh - previous <= 1,
                $"{spread[frame].Fresh - previous} meshed villagers were reshaped in frame {frame + 1}. " +
                "At most one reshape of an entity that already has a mesh may run per frame.");
            Assert.True(spread[frame].AllMeshedAndDrawn,
                $"A villager had no mesh or was not drawn in frame {frame + 1} while its reshape waited. " +
                "A deferred entity must keep drawing its previous mesh.");
        }
        Assert.True(spread[^1].Fresh == Villagers,
            $"Only {spread[^1].Fresh} of {Villagers} villagers were reshaped after {spread.Count} frames. " +
            "Every deferred reshape must still run, one per frame.");

        await WithOptimizationStateRestoredAsync("entityshape", async () =>
        {
            await SetOptimizationAsync("entityshape", enabled: false);
            List<FrameState> vanilla = await ReshapeAllAsync(villagers);
            Console.WriteLine("entityshape off, fresh per frame: " + string.Join(", ", vanilla.Select(s => s.Fresh)));
            Assert.True(vanilla[0].Fresh == Villagers,
                $"With entityshape off only {vanilla[0].Fresh} of {Villagers} villagers were reshaped in the first frame. " +
                "Turning the option off must restore the vanilla behaviour of reshaping all of them at once.");
        });
    }

    /// <summary>
    /// Reports the slowest client frame while eight meshed villagers reshape together, option on
    /// against option off, in alternating rounds. Wall-clock numbers on a shared machine are
    /// reported, not asserted.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    [Trait("Category", "Benchmark")]
    public async Task SlowestFrameWhileEightVillagersReshape()
    {
        long[] villagers = await SpawnVillagersAsync();
        List<string> rows =
        [
            "| Round | entityshape | Slowest frame ms | Median frame ms | All reshaped |",
            "| ---: | --- | ---: | ---: | --- |",
        ];
        await WithOptimizationStateRestoredAsync("entityshape", async () =>
        {
            for (int round = 1; round <= 5; round++)
            {
                foreach (bool enabled in round % 2 == 1 ? new[] { false, true } : new[] { true, false })
                {
                    await SetOptimizationAsync("entityshape", enabled);
                    await Session!.StepFramesAsync(30);
                    Client!.RunOnClientThread(() =>
                    {
                        foreach (long id in villagers)
                        {
                            Client.Client.World.GetEntityById(id)?.MarkShapeModified();
                        }
                        return true;
                    });
                    Sample sample = await MeasureAsync($"entityshape-{(enabled ? "on" : "off")}-{round}", Villagers + 4);
                    bool allFresh = Client.RunOnClientThread(() => ReadState(villagers).Fresh == Villagers);
                    rows.Add($"| {round} | {(enabled ? "on" : "off")} | {sample.Max.TotalMilliseconds:F1} | {sample.Median.TotalMilliseconds:F1} | {(allFresh ? "yes" : "no")} |");
                    await Session.StepFramesAsync(30);
                }
            }
        });

        string report = string.Join('\n', rows);
        Console.WriteLine(report);
        await WriteArtifactAsync(nameof(EntityShapeBudgetTests), "slowest-frame.md", report);
    }

    private async Task<List<FrameState>> ReshapeAllAsync(long[] villagers)
    {
        Client!.RunOnClientThread(() =>
        {
            foreach (long id in villagers)
            {
                Client.Client.World.GetEntityById(id)?.MarkShapeModified();
            }
            return true;
        });

        List<FrameState> frames = [];
        for (int frame = 0; frame < Villagers + 4; frame++)
        {
            await Session!.StepFramesAsync(1);
            frames.Add(Client.RunOnClientThread(() => ReadState(villagers)));
        }

        // Let the worker threads finish the meshes before the next round.
        await Session!.StepFramesAsync(30);
        return frames;
    }

    private FrameState ReadState(long[] villagers)
    {
        var meshRef = AccessTools.Field(AccessTools.TypeByName("Vintagestory.GameContent.EntityShapeRenderer"), "meshRefOpaque");
        int fresh = 0;
        bool allMeshedAndDrawn = true;
        foreach (long id in villagers)
        {
            Entity? entity = Client!.Client.World.GetEntityById(id);
            if (entity?.ShapeFresh == true) fresh++;
            bool meshed = entity?.Properties.Client.Renderer is { } renderer && meshRef.GetValue(renderer) != null;
            allMeshedAndDrawn &= meshed && entity!.IsRendered;
        }
        return new FrameState(fresh, allMeshedAndDrawn);
    }

    /// <summary>
    /// Spawns dressed villagers on a stone platform in front of the camera and waits until
    /// every one has its first mesh.
    /// </summary>
    private async Task<long[]> SpawnVillagersAsync()
    {
        await WarmUpAsync();
        CameraPose pose = await ApplyPoseAsync(OptiTimeConfig.Poses[1]);
        double dx = pose.Target[0] - pose.Eye[0];
        double dz = pose.Target[2] - pose.Eye[2];
        double length = Math.Sqrt(dx * dx + dz * dz);
        dx /= length;
        dz /= length;

        long[] villagers = ServerHost!.RunOnGameThread(() =>
        {
            ICoreServerAPI api = (ICoreServerAPI)ServerHost.Server.Api;
            int stone = api.World.GetBlock(new AssetLocation("game:rock-granite"))?.BlockId
                ?? throw new InvalidOperationException("game:rock-granite is not registered.");
            List<long> ids = [];
            for (int i = 0; i < Villagers; i++)
            {
                double distance = 10 + i / 4 * 5;
                double sideways = (i % 4 - 1.5) * 3;
                Vec3d at = new(
                    Math.Floor(pose.Eye[0] + dx * distance - dz * sideways) + 0.5,
                    Math.Floor(pose.Eye[1]) - 3,
                    Math.Floor(pose.Eye[2] + dz * distance + dx * sideways) + 0.5);
                BlockPos floor = new((int)Math.Floor(at.X), (int)at.Y - 1, (int)Math.Floor(at.Z));
                for (int offsetX = -2; offsetX <= 2; offsetX++)
                for (int offsetZ = -2; offsetZ <= 2; offsetZ++)
                {
                    api.World.BlockAccessor.SetBlock(stone, floor.AddCopy(offsetX, 0, offsetZ));
                }

                string code = i % 2 == 0 ? "game:villager-nadiya-male-generic-generic" : "game:villager-nadiya-female-generic-generic";
                EntityProperties type = api.World.GetEntityType(new AssetLocation(code))
                    ?? throw new InvalidOperationException($"Entity type {code} is not registered.");
                Entity villager = api.World.ClassRegistry.CreateEntity(type);
                villager.Pos.SetPos(at);
                api.World.SpawnEntity(villager);
                ids.Add(villager.EntityId);
            }
            return ids.ToArray();
        });

        bool ready = await StepUntilAsync(() => Client!.RunOnClientThread(() => ReadState(villagers) is { Fresh: Villagers, AllMeshedAndDrawn: true }), maxFrames: 900);
        Assert.True(ready, "The villagers never got a first mesh on the client.");
        await Session!.StepFramesAsync(120);
        return villagers;
    }
}
