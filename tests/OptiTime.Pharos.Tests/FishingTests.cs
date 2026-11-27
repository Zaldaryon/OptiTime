using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Zaldaryon.Pharos.Player;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Casts a real fishing pole through the client's own input, the way a player does.
///
/// <para>Fishing has broken twice under the entity animation optimization. The
/// cast is not a direct call: pressing use starts the player's aim animation,
/// and the bobber is thrown from a frame callback at frame 40 of it, so the
/// player's animation tick has to keep running. The line is a cloth system
/// pinned to the pole tip and to the bobber, and the bobber has to stay drawn
/// for the line to follow it. In 1.5.12 and 1.5.13 the line detached and
/// re-casting spawned duplicate bobbers.</para>
///
/// <para>The cast goes into a walled pool built next to the spawn, so the test
/// does not depend on where the seed happens to put water.</para>
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class FishingTests : OptiTimeScenarioBase
{
    private const string FishingPole = "game:fishingpole-simple-wood";

    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task CastLineStaysOnASingleBobberAndRecastsCleanly()
    {
        await WarmUpAsync();
        Vec3d aim = await BuildPoolAndHandOverThePoleAsync();

        await WithOptimizationStateRestoredAsync("entityanim", async () =>
        {
            foreach (bool enabled in new[] { true, false })
            {
                await SetOptimizationAsync("entityanim", enabled);
                string state = enabled ? "entityanim on" : "entityanim off";

                long first = await CastAsync(aim, state);
                await AssertLineHoldsAsync(first, state);

                await ReelInAsync(aim, state);

                long second = await CastAsync(aim, state + ", second cast");
                Assert.NotEqual(first, second);
                await AssertLineHoldsAsync(second, state + ", second cast");

                await ReelInAsync(aim, state + ", second cast");
            }
        });
    }

    /// <summary>
    /// Builds a soil standing area with a stone-walled pool of still water in front of it,
    /// puts the pole in hotbar slot 0, and returns the point the camera aims at to cast.
    /// </summary>
    private async Task<Vec3d> BuildPoolAndHandOverThePoleAsync()
    {
        long entityId = ((ClientTestPlayer)Player!).RawEntity!.EntityId;
        TaskCompletionSource teleported = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Vec3d aim = ServerHost!.RunOnGameThread(() =>
        {
            ICoreServerAPI api = (ICoreServerAPI)ServerHost.Server.Api;
            IServerPlayer player = ServerHost.Server.PlayersByUid.Values.Single(p => p.Entity.EntityId == entityId);
            var spawn = ServerHost.Server.DefaultSpawnPosition;
            int x = (int)Math.Floor(spawn.X);
            int z = (int)Math.Floor(spawn.Z);
            int floorY = api.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos(x, 0, z));

            int air = 0;
            int soil = BlockId(api, "game:soil-medium-none");
            int stone = BlockId(api, "game:rock-granite");
            int water = BlockId(api, "game:water-still-7");
            IBlockAccessor blocks = api.World.BlockAccessor;

            for (int dx = -4; dx <= 4; dx++)
            for (int dz = -3; dz <= 11; dz++)
            {
                for (int dy = 1; dy <= 6; dy++) blocks.SetBlock(air, new BlockPos(x + dx, floorY + dy, z + dz));

                bool standing = dz <= 0 && Math.Abs(dx) <= 2;
                bool pool = dz >= 1 && dz <= 10 && Math.Abs(dx) <= 3;
                if (standing) blocks.SetBlock(soil, new BlockPos(x + dx, floorY, z + dz));
                if (!pool) continue;

                bool wall = dz == 1 || dz == 10 || Math.Abs(dx) == 3;
                for (int dy = -3; dy <= 0; dy++)
                {
                    bool interior = !wall && dy > -3;
                    BlockPos pos = new(x + dx, floorY + dy, z + dz);
                    blocks.SetBlock(interior ? air : stone, pos);
                    if (interior) blocks.SetBlock(water, pos, BlockLayersAccess.Fluid);
                }
            }

            ItemSlot slot = player.InventoryManager.GetHotbarInventory()?[0]
                ?? throw new InvalidOperationException("The player has no hotbar slot 0.");
            slot.Itemstack = new ItemStack(api.World.GetItem(new AssetLocation(FishingPole))
                ?? throw new InvalidOperationException(FishingPole + " is not registered."));
            slot.MarkDirty();
            player.InventoryManager.ActiveHotbarSlotNumber = 0;

            player.WorldData.FreeMove = false;
            player.WorldData.NoClip = false;
            player.BroadcastPlayerData();
            player.Entity.TeleportToDouble(x + 0.5, floorY + 1, z + 0.5, () => teleported.TrySetResult());

            return new Vec3d(x + 0.5, floorY + 2.5, z + 7.5);
        });

        await StepUntilAsync(() => teleported.Task.IsCompleted, maxFrames: 600);
        Assert.True(teleported.Task.IsCompleted, "The server did not complete the teleport to the fishing spot.");

        Player!.Inventory.SelectHotbarSlot(0);
        bool holding = await StepUntilAsync(() => Client!.RunOnClientThread(() =>
            Client.Client.player?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible.Code.ToString() == FishingPole
            && Client.Client.EntityPlayer.OnGround), maxFrames: 600);
        Assert.True(holding, "The client is not standing on the fishing spot with the pole in hand.");

        return aim;
    }

    private async Task<long> CastAsync(Vec3d aim, string state)
    {
        Assert.Empty(ServerBobbers());

        await PressUseAsync(aim);

        bool thrown = await StepUntilAsync(() => ServerBobbers().Length > 0, maxFrames: 600);
        Assert.True(thrown, $"{state}: pressing use with the pole never threw a bobber. The cast is fired by a frame " +
            "callback of the player's aim animation, so the player's animation tick is not running.");

        // Let the bobber fly, land in the pool and the line settle.
        await Session!.StepFramesAsync(180);

        long[] bobbers = ServerBobbers();
        Assert.True(bobbers.Length == 1, $"{state}: {bobbers.Length} bobbers exist after one cast.");
        return bobbers[0];
    }

    private async Task ReelInAsync(Vec3d aim, string state)
    {
        await PressUseAsync(aim);
        bool reeled = await StepUntilAsync(() => ServerBobbers().Length == 0, maxFrames: 600);
        Assert.True(reeled, $"{state}: pressing use again did not reel the bobber in ({ServerBobbers().Length} left).");
        await Session!.StepFramesAsync(30);
    }

    private async Task PressUseAsync(Vec3d aim)
    {
        Client!.RunOnClientThread(() =>
        {
            Client.TestPlayer.Camera.LookAt(aim);
            return true;
        });
        await Session!.StepFramesAsync(5);

        Client.Controls.Press(PlayerAction.Use);
        try
        {
            await Session.StepFramesAsync(10);
        }
        finally
        {
            Client.Controls.Release(PlayerAction.Use);
        }
        await Session.StepFramesAsync(5);
    }

    private async Task AssertLineHoldsAsync(long bobberId, string state)
    {
        bool arrived = await StepUntilAsync(() => Client!.RunOnClientThread(() =>
            Client.Client.World.GetEntityById(bobberId) is not null), maxFrames: 300);
        Assert.True(arrived, $"{state}: the client never received bobber {bobberId}.");

        // Sample the line over a stretch of frames rather than once: the 1.5.12 defect was the
        // line drifting off the bobber while it bobbed on the water.
        for (int sample = 0; sample < 6; sample++)
        {
            await Session!.StepFramesAsync(20);
            string? problem = Client!.RunOnClientThread(() =>
            {
                ICoreClientAPI capi = (ICoreClientAPI)Client.Client.Api;
                Entity? bobber = Client.Client.World.GetEntityById(bobberId);
                if (bobber is null || !bobber.Alive) return "the bobber is gone on the client";
                if (!bobber.IsRendered) return "the bobber is not drawn";

                int clientBobbers = Client.Client.LoadedEntities.Values.Count(IsLiveBobber);
                if (clientBobbers != 1) return $"the client has {clientBobbers} bobbers";

                object? clothManager = capi.ModLoader.GetModSystem("Vintagestory.GameContent.ClothManager");
                object? line = clothManager is null ? null : AccessTools.Method(clothManager.GetType(), "GetClothSystemAttachedToEntity")
                    ?.Invoke(clothManager, [bobberId]);
                if (line is null) return "no line is attached to the bobber";

                Traverse lineEnd = Traverse.Create(line).Property("LastPoint");
                Traverse lineStart = Traverse.Create(line).Property("FirstPoint");
                if (lineEnd.Property("PinnedToEntity").GetValue<Entity>()?.EntityId != bobberId)
                    return "the far end of the line is not pinned to the bobber";
                if (lineStart.Property("PinnedToEntity").GetValue<Entity>()?.EntityId != Client.Client.EntityPlayer.EntityId)
                    return "the near end of the line is not pinned to the player";

                double gap = lineEnd.Field("Pos").GetValue<Vec3d>().DistanceTo(bobber.Pos.XYZ);
                return gap > 1.0 ? $"the end of the line is {gap:F2} blocks away from the bobber" : null;
            });

            Assert.True(problem is null, $"{state}, sample {sample}: {problem}.");
        }
    }

    private long[] ServerBobbers() => ServerHost!.RunOnGameThread(() =>
        ((ICoreServerAPI)ServerHost.Server.Api).World.LoadedEntities.Values
            .Where(IsLiveBobber)
            .Select(entity => entity.EntityId)
            .ToArray());

    private static bool IsLiveBobber(Entity entity) =>
        entity.Alive && entity.Code?.Path.StartsWith("bobber", StringComparison.Ordinal) == true;

    private static int BlockId(ICoreServerAPI api, string code) =>
        api.World.GetBlock(new AssetLocation(code))?.BlockId
            ?? throw new InvalidOperationException(code + " is not registered.");
}
