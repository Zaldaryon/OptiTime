using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Zaldaryon.Pharos.Player;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Drives prepared vanilla outputs through the crafting result slot and their
/// real held-item use paths. Ingredients are loaded directly into the crafting
/// grid, so these cases exercise output transfer and use but do not reproduce
/// ingredient gathering or any reporter's third-party mod list/save.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: "surviveandbuild",
    worldType: "superflat")]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class ModDbReportRelatedTests : OptiTimeScenarioBase
{
    private const int HealingApplicationHoldFrames = 186; // 3.1 seconds at 60 Hz; vanilla requires 3 seconds.
    private const int FirestarterIgnitionAttempts = 64; // Bound retries for vanilla's 25% ignition roll.

    /// <summary>
    /// Crafts a vanilla bandage with the recipe cache on, turns the saved setting
    /// off, then uses the prepared item. The live Harmony patch remains loaded
    /// until restart, so this is not an unpatched control or a reproduction of
    /// the reporter's mod list or save.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task PreparedBandageRemainsUsableAfterRecipeSettingTurnsOff()
    {
        await WarmUpAsync();
        await WithOptimizationStateRestoredAsync("recipe", async () =>
        {
            await EnsurePlayerOnGroundAsync();
            await VerifyHealingOutputAsync(
                "game:bandage-clean",
                hotbarSlot: 0,
                recipeSettingEnabled: true,
                turnRecipeSettingOffBeforeUse: true);
            AssertNoUnexpectedClientErrors("Prepared bandage after changing recipe setting");
        });
    }

    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task PreparedBandageRemainsUsableWithRecipeSettingOn()
    {
        await WarmUpAsync();
        await WithOptimizationStateRestoredAsync("recipe", async () =>
        {
            await EnsurePlayerOnGroundAsync();
            await VerifyHealingOutputAsync("game:bandage-clean", hotbarSlot: 0, recipeSettingEnabled: true);
            AssertNoUnexpectedClientErrors("Prepared bandage with recipe cache enabled");
        });
    }

    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task PreparedPoulticeRemainsUsableWithinTheSameSession()
    {
        await WarmUpAsync();
        await WithOptimizationStateRestoredAsync("recipe", async () =>
        {
            await EnsurePlayerOnGroundAsync();
            await VerifyHealingOutputAsync("game:poultice-linen-horsetail", hotbarSlot: 0, recipeSettingEnabled: true);
            AssertNoUnexpectedClientErrors("Prepared poultice");
        });
    }

    /// <summary>
    /// Uses the vanilla firestarter against a firepit loaded with four pieces
    /// of firewood, matching the in-game construction instructions. The
    /// vanilla stop handler has a random 25% ignition roll, so the test retries
    /// actual client input until ignition succeeds or the bounded attempt count
    /// is exhausted.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task PreparedFirestarterIgnitesAFueledVanillaFirepitThroughClientInput()
    {
        await WarmUpAsync();
        await WithOptimizationStateRestoredAsync("recipe", async () =>
        {
            await SetOptimizationAsync("recipe", enabled: true);
            (ItemStack clientOutput, ItemStack serverOutput) =
                await PrepareRecipeOutputForUseAsync("game:firestarter", hotbarSlot: 0, recipeSettingEnabled: true);
            Assert.Equal("game:firestarter", clientOutput.Collectible.Code.ToString());
            Assert.Equal("game:firestarter", serverOutput.Collectible.Code.ToString());
            Assert.Equal(clientOutput.StackSize, serverOutput.StackSize);
            Assert.Equal("game:firestarter", ServerActiveItemCode());

            BlockPos firepit = await PlaceFueledFirepitAsync();
            Assert.True(await Session!.Blocks.AimAtAsync(firepit),
                "The client could not select the fueled vanilla firepit.");
            string initialState = ServerBlockCode(firepit);
            string initialClientState = ClientBlockCode(firepit);
            Assert.Contains("firepit", initialState, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(initialState, initialClientState);
            Assert.False(initialState.EndsWith("firepit-lit", StringComparison.OrdinalIgnoreCase),
                $"The firepit was already lit before the firestarter was used: {initialState}.");
            string serverIgnitionState = ServerHost!.RunOnGameThread(() =>
            {
                BlockEntity entity = ((ICoreServerAPI)ServerHost.Server.Api).World.BlockAccessor.GetBlockEntity(firepit)
                    ?? throw new InvalidOperationException("The fueled firepit block entity is unavailable on the server.");
                return FirepitIgnitionState(entity, secondsIgniting: 0f);
            });
            Assert.Equal("Ignitable", serverIgnitionState);
            string firepitReadyState = ServerHost.RunOnGameThread(() =>
            {
                BlockEntity entity = ((ICoreServerAPI)ServerHost.Server.Api).World.BlockAccessor.GetBlockEntity(firepit)
                    ?? throw new InvalidOperationException("The fueled firepit block entity is unavailable on the server.");
                return FirepitIgnitionState(entity, secondsIgniting: 3.1f);
            });
            Assert.Equal("IgniteNow", firepitReadyState);

            bool clientFuelSynced = await StepUntilAsync(() => Client!.RunOnClientThread(() =>
            {
                BlockEntity? entity = Client.Client.World.BlockAccessor.GetBlockEntity(firepit);
                return entity is not null && FirepitIgnitionState(entity, secondsIgniting: 0f) == "Ignitable";
            }), maxFrames: 30);
            Assert.True(clientFuelSynced,
                "The server's fueled firepit inventory never reached the client as an ignitable block.");

            int durabilityBefore = ServerActiveItemDurability();
            int attempts = 0;
            var attemptResults = new List<string>();
            for (; attempts < FirestarterIgnitionAttempts &&
                 !ServerBlockCode(firepit).EndsWith("firepit-lit", StringComparison.OrdinalIgnoreCase);
                 attempts++)
            {
                Assert.True(await Session.Blocks.AimAtAsync(firepit),
                    "The client could not select the fueled vanilla firepit.");
                Assert.Equal("game:firestarter", ServerActiveItemCode());
                bool interactionStarted = await UseActiveItemForDurationAsync(
                    "game:firestarter", holdFrames: 190, sneaking: true);
                string activeItemAfter = ServerActiveItemCode() ?? "<empty>";
                string stateAfter = ServerBlockCode(firepit);
                attemptResults.Add(
                    $"attempt={attempts + 1} started={interactionStarted} activeItem={activeItemAfter} state={stateAfter}");
                await WriteArtifactAsync(nameof(ModDbReportRelatedTests), "firepit-attempts.txt",
                    string.Join('\n', attemptResults) + "\n");
                Assert.True(interactionStarted,
                    $"The prepared firestarter did not begin a held-item interaction on attempt {attempts + 1}; " +
                    $"activeItem={activeItemAfter}, state={stateAfter}.");
                Assert.Equal("game:firestarter", activeItemAfter);
            }

            bool litAndSynced = await StepUntilAsync(() =>
                ServerBlockCode(firepit).EndsWith("firepit-lit", StringComparison.OrdinalIgnoreCase) &&
                ClientBlockCode(firepit).EndsWith("firepit-lit", StringComparison.OrdinalIgnoreCase),
                maxFrames: 180);
            int durabilityAfter = ServerActiveItemDurability();
            await WriteArtifactAsync(nameof(ModDbReportRelatedTests), "firepit-ignition.txt",
                $"attempts={attempts}\n" +
                "holdInputFramesPerAttempt=190\n" +
                $"initialState={initialState}\n" +
                $"initialClientState={initialClientState}\n" +
                string.Join('\n', attemptResults) + "\n" +
                $"serverState={ServerBlockCode(firepit)}\n" +
                $"clientState={ClientBlockCode(firepit)}\n" +
                $"durabilityBefore={durabilityBefore}\n" +
                $"durabilityAfter={durabilityAfter}\n");
            Assert.True(litAndSynced,
                $"The fueled firepit did not light on the server and client after {attempts} held interactions.");
            Assert.True(durabilityAfter < durabilityBefore,
                "The firestarter durability did not change after the vanilla firepit lit.");
            AssertNoUnexpectedClientErrors("Prepared firestarter on a fueled vanilla firepit");
        });
    }

    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task PreparedFirestarterIgnitesAnExtinctVanillaTorchThroughClientInput()
    {
        await WarmUpAsync();
        await WithOptimizationStateRestoredAsync("recipe", async () =>
        {
            await SetOptimizationAsync("recipe", enabled: true);
            (ItemStack clientOutput, ItemStack serverOutput) =
                await PrepareRecipeOutputForUseAsync("game:firestarter", hotbarSlot: 0, recipeSettingEnabled: true);
            Assert.Equal("game:firestarter", clientOutput.Collectible.Code.ToString());
            Assert.Equal("game:firestarter", serverOutput.Collectible.Code.ToString());
            Assert.Equal("game:firestarter", ServerActiveItemCode());

            BlockPos torchPosition = await PlaceExtinctTorchAsync();
            string extinctState = ServerBlockCode(torchPosition);
            Assert.Equal("game:torch-basic-extinct-up", extinctState);
            string thresholdBefore = TorchIgnitionState(torchPosition, secondsIgniting: 1f);
            string thresholdAfter = TorchIgnitionState(torchPosition, secondsIgniting: 1.1f);
            Assert.Equal("Ignitable", thresholdBefore);
            Assert.Equal("IgniteNow", thresholdAfter);

            int durabilityBefore = ServerActiveItemDurability();
            int attempts = 0;
            for (; attempts < FirestarterIgnitionAttempts &&
                 !ServerBlockCode(torchPosition).EndsWith("torch-basic-lit-up", StringComparison.OrdinalIgnoreCase);
                 attempts++)
            {
                Assert.True(await Session!.Blocks.AimAtAsync(torchPosition),
                    "The client could not select the extinct vanilla torch.");
                bool interactionStarted = await UseActiveItemForDurationAsync(
                    "game:firestarter", holdFrames: 66);
                Assert.True(interactionStarted,
                    $"The prepared firestarter did not begin a held-item interaction on torch attempt {attempts + 1}.");
            }

            bool litAndSynced = await StepUntilAsync(() =>
                ServerBlockCode(torchPosition).EndsWith("torch-basic-lit-up", StringComparison.OrdinalIgnoreCase) &&
                ClientBlockCode(torchPosition).EndsWith("torch-basic-lit-up", StringComparison.OrdinalIgnoreCase),
                maxFrames: 180);
            int durabilityAfter = ServerActiveItemDurability();
            await WriteArtifactAsync(nameof(ModDbReportRelatedTests), "torch-ignition.txt",
                $"attempts={attempts}\n" +
                $"maxUseInputFramesPerAttempt=66\n" +
                $"maxUseInputSecondsPerAttempt={66f / 60f:F3}\n" +
                $"serverState={ServerBlockCode(torchPosition)}\n" +
                $"clientState={ClientBlockCode(torchPosition)}\n" +
                $"durabilityBefore={durabilityBefore}\n" +
                $"durabilityAfter={durabilityAfter}\n");
            Assert.True(litAndSynced,
                $"The extinct torch did not light on the server and client after {attempts} held interactions.");
            Assert.True(durabilityAfter < durabilityBefore,
                "The firestarter durability did not change after the vanilla torch lit.");
            AssertNoUnexpectedClientErrors("Prepared firestarter on an extinct vanilla torch");
        });
    }

    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task VanillaIronAnvilCanBePlacedThroughClientInput()
    {
        await WarmUpAsync();
        BlockPos ground = PlayerBlockPosition(2);
        BlockPos anvil = ground.UpCopy();

        Assert.True(await Session!.Blocks.PlaceAsync(ground, BlockFacing.UP, "game:anvil-iron"),
            "The vanilla iron anvil did not place through the client's world interaction.");
        Assert.Equal("game:anvil-iron", ServerBlockCode(anvil));
        Assert.Equal("game:anvil-iron", ClientBlockCode(anvil));
        Assert.NotNull(ServerHost!.RunOnGameThread(() =>
            ((ICoreServerAPI)ServerHost.Server.Api).World.BlockAccessor.GetBlockEntity(anvil)));
        AssertNoUnexpectedClientErrors("Placing a vanilla iron anvil through the client");
    }

    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task PreparedCopperScytheBreaksVanillaTallGrassWithoutReconnect()
    {
        await WarmUpAsync();
        await EnsurePlayerOnGroundAsync();
        var client = Client ?? throw new InvalidOperationException("Pharos did not provide a client for the scythe scenario.");
        await WithOptimizationStateRestoredAsync("recipe", async () =>
        {
            await SetOptimizationAsync("recipe", enabled: true);
            (ItemStack clientOutput, ItemStack serverOutput) =
                await PrepareRecipeOutputForUseAsync("game:scythe-copper", hotbarSlot: 0, recipeSettingEnabled: true);
            Assert.Equal("game:scythe-copper", clientOutput.Collectible.Code.ToString());
            Assert.Equal("game:scythe-copper", serverOutput.Collectible.Code.ToString());
            Assert.Equal(clientOutput.StackSize, serverOutput.StackSize);
            Assert.Equal("game:scythe-copper", ServerActiveItemCode());

            Assert.Equal(0, ClientActiveItemToolMode());
            Assert.Equal(0, ServerActiveItemToolMode());
            Assert.NotNull(Client!.Hotkeys.Get("toolmodeselect"));
            await Client.Hotkeys.TriggerAsync("toolmodeselect");
            Assert.True(Client.Ui!.IsOpen("GuiDialogToolMode"),
                "The normal tool-mode hotkey did not open the vanilla scythe mode selector.");
            var toolModeGrid = Client.Ui.Find("GuiDialogToolMode", "skillitemgrid-0");
            Assert.NotNull(toolModeGrid);
            Assert.True(toolModeGrid.IsInteractive && toolModeGrid.Width > 1 && toolModeGrid.Height > 1,
                "The vanilla tool-mode grid is not available for a real mouse click.");
            Client.Input.Click(
                (int)(toolModeGrid.X + toolModeGrid.Width * 0.75),
                (int)(toolModeGrid.Y + toolModeGrid.Height / 2));
            bool removeModeSynced = await StepUntilAsync(
                () => ClientActiveItemToolMode() == 1 && ServerActiveItemToolMode() == 1,
                maxFrames: 120);
            Assert.True(removeModeSynced,
                $"Selecting remove-grass mode through the vanilla dialog did not synchronize to both peers; " +
                $"client={ClientActiveItemToolMode()}, server={ServerActiveItemToolMode()}.");

            BlockPos grass = await PlaceTallGrassNearPlayerAsync();
            Assert.Equal("game:tallgrass-medium-free", ServerBlockCode(grass));
            Vec3d grassAimPoint = new(grass.X + 0.5, grass.Y + 0.125, grass.Z + 0.5);
            bool grassSelected = await StepUntilAsync(() => client.RunOnClientThread(() =>
            {
                client.TestPlayer.Camera.LookAt(grassAimPoint);
                return client.Client.BlockSelection?.Position?.Equals(grass) == true;
            }), maxFrames: 120);
            Assert.True(grassSelected,
                "The client could not select the vanilla tall grass at a point inside its 0.25-block selection box.");

            client.Controls.Press(PlayerAction.Attack);
            bool brokeOnClient;
            try
            {
                brokeOnClient = await StepUntilAsync(
                    () => ClientBlockCode(grass) == "game:air",
                    maxFrames: 600);
            }
            finally
            {
                client.Controls.Release(PlayerAction.Attack);
            }
            Assert.True(brokeOnClient,
                "The vanilla client did not break the selected tall grass with the prepared copper scythe.");

            bool removalSynced = await StepUntilAsync(
                () => ServerBlockCode(grass) == "game:air" && ClientBlockCode(grass) == "game:air",
                maxFrames: 180);
            await WriteArtifactAsync(nameof(ModDbReportRelatedTests), "scythe-use.txt",
                $"item={ServerActiveItemCode()}\n" +
                $"toolMode={ServerActiveItemToolMode()}\n" +
                $"blockAfter={ServerBlockCode(grass)}\n" +
                $"clientBlockAfter={ClientBlockCode(grass)}\n");

            Assert.True(removalSynced,
                "Breaking tall grass with the prepared copper scythe did not synchronize to client and server.");
            AssertNoUnexpectedClientErrors("Prepared copper scythe on vanilla tall grass");
        });
    }

    private async Task VerifyHealingOutputAsync(
        string outputCode,
        int hotbarSlot,
        bool recipeSettingEnabled,
        bool turnRecipeSettingOffBeforeUse = false)
    {
        await SetOptimizationAsync("recipe", recipeSettingEnabled);
        (ItemStack clientOutput, ItemStack serverOutput) =
            await PrepareRecipeOutputForUseAsync(outputCode, hotbarSlot, recipeSettingEnabled);
        Assert.Equal(outputCode, clientOutput.Collectible.Code.ToString());
        Assert.Equal(outputCode, serverOutput.Collectible.Code.ToString());
        Assert.Equal(clientOutput.StackSize, serverOutput.StackSize);
        Assert.Equal(outputCode, ServerActiveItemCode());

        if (turnRecipeSettingOffBeforeUse)
        {
            await SetOptimizationAsync("recipe", enabled: false);
        }

        int countBefore = ServerActiveItemCount();
        DamageServerPlayer(3);
        await Session!.StepFramesAsync(5);
        float healthBefore = ServerHealth();
        float clientHealthBefore = ClientHealth();
        int clientLogCountBefore = Client!.Logs.Entries.Count;
        Assert.True(ServerPlayerIsOnGround(),
            $"The loopback player left the ground before applying '{outputCode}'.");

        bool interactionStarted = await UseActiveItemForDurationAsync(
            outputCode,
            holdFrames: HealingApplicationHoldFrames);
        bool healed = await StepUntilAsync(() => ServerHealth() > healthBefore, maxFrames: 600);
        await Session.StepFramesAsync(5);
        string recentClientLog = string.Join('\n', Client.Logs.Entries
            .Skip(clientLogCountBefore)
            .Select(entry => entry.Message));

        int remaining = ServerActiveItemCount();
        Assert.True(remaining < countBefore,
            $"Using prepared '{outputCode}' did not consume its active stack " +
            $"(recipeSettingAtCraft={recipeSettingEnabled}, turnedOffBeforeUse={turnRecipeSettingOffBeforeUse}, " +
            $"before={countBefore}, after={remaining}).");
        Assert.True(interactionStarted,
            $"Using '{outputCode}' did not start the held-item interaction " +
            $"(recipeSettingAtCraft={recipeSettingEnabled}, turnedOffBeforeUse={turnRecipeSettingOffBeforeUse}).");
        Assert.True(healed,
            $"Using prepared '{outputCode}' did not increase server health " +
            $"(recipeSettingAtCraft={recipeSettingEnabled}, turnedOffBeforeUse={turnRecipeSettingOffBeforeUse}) " +
            $"from {healthBefore:F2}.");
        Assert.Contains("Gained", recentClientLog, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("through heal", recentClientLog, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(remaining, ClientActiveItemCount());
        float clientHealthAfter = ClientHealth();
        Assert.True(clientHealthAfter > clientHealthBefore,
            $"The client health did not increase after using '{outputCode}' " +
            $"(before={clientHealthBefore:F2}, after={clientHealthAfter:F2}).");
        Assert.True(Math.Abs(clientHealthAfter - ServerHealth()) < 0.05f,
            $"Client and server health did not synchronize after using '{outputCode}'.");
    }

    private async Task<bool> UseActiveItemForDurationAsync(string outputCode, int holdFrames, bool sneaking = false)
    {
        if (sneaking)
        {
            Client!.Controls.Press(PlayerAction.Sneak);
        }
        Client!.Controls.Press(PlayerAction.Use);
        bool started = false;
        try
        {
            await Session!.StepFramesAsync(5);
            started = Client.RunOnClientThread(() =>
                Client.Client.EntityPlayer.Controls.HandUse == EnumHandInteract.HeldItemInteract);
            await Session.StepFramesAsync(holdFrames - 5);
        }
        finally
        {
            Client.Controls.Release(PlayerAction.Use);
            if (sneaking)
            {
                Client.Controls.Release(PlayerAction.Sneak);
            }
            await Session!.StepFramesAsync(10);
        }

        bool completed = await StepUntilAsync(
            () => Client!.RunOnClientThread(() =>
                Client.Client.EntityPlayer.Controls.HandUse == EnumHandInteract.None),
            maxFrames: 60);
        Assert.True(completed,
            $"Using '{outputCode}' did not leave the held-item interaction state after releasing input.");
        return started;
    }

    private ItemStack GenerateClientRecipeOutput(string outputCode) => Client!.RunOnClientThread(() =>
    {
        IInventory grid = Player!.Inventory.Crafting
            ?? throw new InvalidOperationException("The client crafting grid is unavailable.");
        IPlayer player = Client.Client.player
            ?? throw new InvalidOperationException("The client player is unavailable for recipe matching.");
        return GenerateRecipeOutput(Client.Client.World, grid, player, outputCode, requireCacheHit: true);
    });

    private async Task<(ItemStack Client, ItemStack Server)> PrepareRecipeOutputForUseAsync(
        string outputCode,
        int hotbarSlot,
        bool recipeSettingEnabled)
    {
        if (recipeSettingEnabled)
        {
            await ResetDiagAsync("recipe");
        }

        ItemStack clientOutput = GenerateClientRecipeOutput(outputCode);
        if (recipeSettingEnabled)
        {
            await RunCommandAsync($"{OptiTimeConfig.Command} diag recipe dump");
            DiagCounters counters = DiagReader.Parse(DiagReader.Body(ClientLogText(), "recipe"));
            Assert.True(counters.Require(DiagCounter.Hits, "recipe") > 0,
                $"The client did not record a cache hit while preparing '{outputCode}'.");
        }

        ItemStack serverOutput = GenerateServerRecipeOutput(outputCode);
        await OpenDialogRequiredAsync("GuiDialogInventory", "inventorydialog");
        Client!.Ui!.ClickSlot("GuiDialogInventory", "outputslot", 0);
        await Session!.StepFramesAsync(10);
        Client.Ui.ClickSlot("HudHotbar", "hotbargrid", hotbarSlot);
        await Session.StepFramesAsync(10);
        Client.Ui.Close("GuiDialogInventory");
        await Session.StepFramesAsync(5);
        // Slot clicks leave the virtual pointer over the HUD hotbar. Held-item
        // input would hit that GUI again until the pointer returns to the world.
        Client.Input.InjectMouseMove(640, 360);
        await WaitForActiveItemAsync(outputCode);
        Assert.Equal(ServerActiveItemCount(), ClientActiveItemCount());
        return (clientOutput, serverOutput);
    }

    private ItemStack GenerateServerRecipeOutput(string outputCode) => ServerHost!.RunOnGameThread(() =>
    {
        ICoreServerAPI api = (ICoreServerAPI)ServerHost.Server.Api;
        IServerPlayer player = ServerHost.Server.GetClientByPlayername(PlayerName)?.Player
            ?? throw new InvalidOperationException("The loopback player is unavailable on the server.");
        IInventory grid = player.InventoryManager.GetOwnInventory(GlobalConstants.craftingInvClassName)
            ?? throw new InvalidOperationException("The server crafting grid is unavailable.");

        _ = GenerateRecipeOutput(api.World, grid, player, outputCode, requireCacheHit: false);

        ItemSlot output = grid[grid.Count - 1]
            ?? throw new InvalidOperationException("The server crafting output slot is unavailable.");
        ItemStack crafted = output.Itemstack?.Clone()
            ?? throw new InvalidOperationException($"Vanilla produced no output for '{outputCode}'.");

        return crafted;
    });

    private static ItemStack GenerateRecipeOutput(
        IWorldAccessor world,
        IInventory grid,
        IPlayer player,
        string outputCode,
        bool requireCacheHit)
    {
        GridRecipe[] recipes = world.GridRecipes.Where(candidate =>
                string.Equals(
                    candidate.Output?.ResolvedItemStack?.Collectible.Code.ToString(),
                    outputCode,
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.Shapeless)
            .ToArray();
        Assert.NotEmpty(recipes);

        Assert.True(grid.Count >= 10,
            $"The live crafting grid has {grid.Count} slots; a 3x3 grid and output slot are required.");

        MethodInfo findMatchingRecipe = grid.GetType().GetMethod(
                "FindMatchingRecipe",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(grid.GetType().FullName, "FindMatchingRecipe");

        GridRecipe? recipe = null;
        var rejected = new List<string>();

        foreach (GridRecipe candidate in recipes)
        {
            if (candidate.ResolvedIngredients is not { Length: > 0 } ingredients)
            {
                rejected.Add($"{candidate.Name}: no resolved inputs");
                continue;
            }

            for (int slotIndex = 0; slotIndex < 9; slotIndex++)
            {
                grid[slotIndex]!.Itemstack = null;
            }

            bool usable = true;
            for (int i = 0; i < ingredients.Length; i++)
            {
                CraftingRecipeIngredient? ingredient = ingredients[i];
                if (ingredient is null)
                {
                    continue;
                }

                ItemStack? ingredientStack = ResolveRecipeIngredientStack(world, ingredient);
                if (ingredientStack is null)
                {
                    usable = false;
                    rejected.Add($"{candidate.Name}: input {i} has no stack satisfying {ingredient}");
                    break;
                }

                int gridIndex = candidate.Shapeless
                    ? i
                    : (i / candidate.Width) * 3 + (i % candidate.Width);
                grid[gridIndex]!.Itemstack = ingredientStack;
                grid[gridIndex]!.MarkDirty();
            }

            if (!usable)
            {
                continue;
            }

            ItemSlot[] inputSlots = Enumerable.Range(0, 9).Select(index => grid[index]!).ToArray();
            if (!candidate.Matches(player, world, inputSlots, 3))
            {
                string inputs = string.Join(", ", inputSlots
                    .Where(slot => slot.Itemstack is not null)
                    .Select(slot => $"{slot.Itemstack!.Collectible.Code}x{slot.Itemstack.StackSize}"));
                rejected.Add($"{candidate.Name}: vanilla Matches rejected [{inputs}]");
                continue;
            }

            recipe = candidate;
            break;
        }

        if (recipe is null)
        {
            throw new InvalidOperationException(
                $"No loaded recipe for '{outputCode}' could be prepared through vanilla matching: " +
                string.Join("; ", rejected));
        }

        findMatchingRecipe.Invoke(grid, null);
        ItemStack first = grid[grid.Count - 1]!.Itemstack?.Clone()
            ?? throw new InvalidOperationException(
                $"The live recipe grid rejected '{recipe.Name}' for '{outputCode}' after vanilla Matches accepted it.");

        if (requireCacheHit)
        {
            findMatchingRecipe.Invoke(grid, null);
        }

        ItemStack result = grid[grid.Count - 1]!.Itemstack?.Clone()
            ?? throw new InvalidOperationException($"The recipe cache removed output '{outputCode}'.");
        Assert.Equal(outputCode, result.Collectible.Code.ToString());
        Assert.Equal(first.StackSize, result.StackSize);
        return result;
    }

    private static ItemStack? ResolveRecipeIngredientStack(
        IWorldAccessor world,
        CraftingRecipeIngredient ingredient)
    {
        if (ingredient.ResolvedItemStack is { } resolved)
        {
            ItemStack stack = resolved.Clone();
            stack.StackSize = ingredient.Quantity;
            if (ingredient.SatisfiesAsIngredient(stack))
            {
                return stack;
            }
        }

        IEnumerable<ItemStack> candidates;
        if (ingredient.Code is { } code)
        {
            candidates = world.SearchBlocks(code)
                .Where(block => block?.Code is not null)
                .Select(block => new ItemStack(block, ingredient.Quantity))
                .Concat(world.SearchItems(code)
                    .Where(item => item?.Code is not null)
                    .Select(item => new ItemStack(item, ingredient.Quantity)));
        }
        else
        {
            candidates = world.Blocks
                .Where(block => block?.Code is not null)
                .Select(block => new ItemStack(block, ingredient.Quantity))
                .Concat(world.Items
                    .Where(item => item?.Code is not null)
                    .Select(item => new ItemStack(item, ingredient.Quantity)));
        }

        return candidates.FirstOrDefault(candidate => ingredient.SatisfiesAsIngredient(candidate));
    }

    private static string FirepitIgnitionState(BlockEntity firepit, float secondsIgniting)
    {
        MethodInfo getIgnitableState = firepit.GetType().GetMethod(
                "GetIgnitableState",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(firepit.GetType().FullName, "GetIgnitableState");
        return getIgnitableState.Invoke(firepit, [secondsIgniting])?.ToString()
            ?? throw new InvalidOperationException("The firepit returned no ignition state.");
    }

    private string TorchIgnitionState(BlockPos position, float secondsIgniting) => ServerHost!.RunOnGameThread(() =>
    {
        ICoreServerAPI api = (ICoreServerAPI)ServerHost.Server.Api;
        Block torch = api.World.BlockAccessor.GetBlock(position);
        MethodInfo onTryIgniteBlock = torch.GetType().GetMethod(
                "OnTryIgniteBlock",
                BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(torch.GetType().FullName, "OnTryIgniteBlock");
        EntityAgent player = (EntityAgent)(ServerHost.Server.GetClientByPlayername(PlayerName)?.Player.Entity
            ?? throw new InvalidOperationException("The loopback player has no server entity."));
        return onTryIgniteBlock.Invoke(torch, [player, position, secondsIgniting])?.ToString()
            ?? throw new InvalidOperationException("The vanilla torch returned no ignition state.");
    });

    private async Task<BlockPos> PlaceExtinctTorchAsync()
    {
        BlockPos position = PlayerBlockPosition(2).UpCopy();
        ServerHost!.RunOnGameThread(() =>
        {
            ICoreServerAPI api = (ICoreServerAPI)ServerHost.Server.Api;
            Block block = api.World.GetBlock(new AssetLocation("game:torch-basic-extinct-up"))
                ?? throw new InvalidOperationException("The vanilla extinct upright torch block is unavailable.");
            api.World.BlockAccessor.SetBlock(block.BlockId, position);
        });

        bool synced = await StepUntilAsync(
            () => ClientBlockCode(position).Equals("game:torch-basic-extinct-up", StringComparison.OrdinalIgnoreCase),
            maxFrames: 120);
        Assert.True(synced, "The server's extinct vanilla torch did not reach the client.");
        return position;
    }

    private async Task<BlockPos> PlaceTallGrassNearPlayerAsync()
    {
        BlockPos position = PlayerBlockPosition(3).UpCopy();
        ServerHost!.RunOnGameThread(() =>
        {
            ICoreServerAPI api = (ICoreServerAPI)ServerHost.Server.Api;
            Block grass = api.World.GetBlock(new AssetLocation("game:tallgrass-medium-free"))
                ?? throw new InvalidOperationException("The vanilla medium tall grass block is unavailable.");
            api.World.BlockAccessor.SetBlock(grass.BlockId, position);
        });

        bool synced = await StepUntilAsync(
            () => Client!.RunOnClientThread(() =>
                Client.Client.World.BlockAccessor.GetBlock(position).Code.Path == "tallgrass-medium-free"),
            maxFrames: 600);
        Assert.True(synced, "The tall grass fixture did not reach the client.");
        return position;
    }

    private async Task WaitForActiveItemAsync(string expectedCode)
    {
        bool synced = await StepUntilAsync(
            () => Client!.RunOnClientThread(() =>
                string.Equals(
                    Client.Client.player?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible.Code.ToString(),
                    expectedCode,
                    StringComparison.OrdinalIgnoreCase)),
            maxFrames: 300);

        Assert.True(synced,
            $"The freshly generated '{expectedCode}' stack did not reach the client's active hand.");
    }

    private int ServerActiveItemCount() => ServerHost!.RunOnGameThread(() =>
    {
        IServerPlayer player = ServerHost.Server.GetClientByPlayername(PlayerName)?.Player
            ?? throw new InvalidOperationException("The loopback player is unavailable on the server.");
        return player.InventoryManager.ActiveHotbarSlot.Itemstack?.StackSize ?? 0;
    });

    private int ClientActiveItemToolMode() => Client!.RunOnClientThread(() =>
        Client.Client.player?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Attributes.GetInt("toolMode", 0) ?? -1);

    private int ServerActiveItemToolMode() => ServerHost!.RunOnGameThread(() =>
    {
        IServerPlayer player = ServerHost.Server.GetClientByPlayername(PlayerName)?.Player
            ?? throw new InvalidOperationException("The loopback player is unavailable on the server.");
        return player.InventoryManager.ActiveHotbarSlot.Itemstack?.Attributes.GetInt("toolMode", 0) ?? -1;
    });

    private void DamageServerPlayer(float amount) => ServerHost!.RunOnGameThread(() =>
    {
        IServerPlayer player = ServerHost.Server.GetClientByPlayername(PlayerName)?.Player
            ?? throw new InvalidOperationException("The loopback player is unavailable on the server.");
        EntityPlayer entity = player.Entity
            ?? throw new InvalidOperationException("The loopback player has no server entity.");
        entity.ReceiveDamage(new DamageSource
        {
            Source = EnumDamageSource.Internal,
            Type = EnumDamageType.Gravity,
            IgnoreInvFrames = true,
        }, amount);
    });

    private async Task EnsurePlayerOnGroundAsync()
    {
        TaskCompletionSource teleported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ServerHost!.RunOnGameThread(() =>
        {
            ICoreServerAPI api = (ICoreServerAPI)ServerHost!.Server.Api;
            IServerPlayer player = ServerHost.Server.GetClientByPlayername(PlayerName)?.Player
                ?? throw new InvalidOperationException("The loopback player is unavailable on the server.");
            EntityPlayer entity = player.Entity
                ?? throw new InvalidOperationException("The loopback player has no server entity.");
            var spawn = ServerHost.Server.DefaultSpawnPosition;
            int x = (int)Math.Floor(spawn.X);
            int z = (int)Math.Floor(spawn.Z);
            int floorY = api.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos(x, 0, z));
            Block floor = api.World.GetBlock(new AssetLocation("game:soil-medium-none"))
                ?? throw new InvalidOperationException("The vanilla medium soil block is unavailable.");

            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dz = -2; dz <= 2; dz++)
                {
                    api.World.BlockAccessor.SetBlock(
                        floor.BlockId,
                        new BlockPos(x + dx, floorY, z + dz));
                }
            }

            player.WorldData.FreeMove = false;
            player.WorldData.NoClip = false;
            player.BroadcastPlayerData();
            entity.TeleportToDouble(x + 0.5, floorY + 1, z + 0.5,
                () => teleported.TrySetResult());
        });

        await StepUntilAsync(() => teleported.Task.IsCompleted, maxFrames: 600);
        Assert.True(teleported.Task.IsCompleted, "The server did not complete the ground-fixture teleport.");
        bool landed = await StepUntilAsync(ServerPlayerIsOnGround, maxFrames: 600);
        Assert.True(landed, "The loopback player did not settle onto the prepared vanilla stone floor.");
        await Session!.StepFramesAsync(10);
    }

    private bool ServerPlayerIsOnGround() => ServerHost!.RunOnGameThread(() =>
        ServerHost.Server.GetClientByPlayername(PlayerName)?.Player?.Entity?.OnGround ?? false);

    private float ServerHealth() => ServerHost!.RunOnGameThread(() =>
    {
        IServerPlayer player = ServerHost.Server.GetClientByPlayername(PlayerName)?.Player
            ?? throw new InvalidOperationException("The loopback player is unavailable on the server.");
        return player.Entity?.WatchedAttributes.GetTreeAttribute("health")?.GetFloat("currenthealth")
            ?? throw new InvalidOperationException("The server player has no current health attribute.");
    });

    private float ClientHealth() => Client!.RunOnClientThread(() =>
        Client.Client.EntityPlayer?.WatchedAttributes.GetTreeAttribute("health")?.GetFloat("currenthealth")
        ?? throw new InvalidOperationException("The client player has no current health attribute."));

    private int ClientActiveItemCount() => Client!.RunOnClientThread(() =>
        Client.Client.player?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.StackSize ?? 0);

    private string? ServerActiveItemCode() => ServerHost!.RunOnGameThread(() =>
    {
        IServerPlayer player = ServerHost.Server.GetClientByPlayername(PlayerName)?.Player
            ?? throw new InvalidOperationException("The loopback player is unavailable on the server.");
        return player.InventoryManager.ActiveHotbarSlot.Itemstack?.Collectible.Code.ToString();
    });

    private int ServerActiveItemDurability() => ServerHost!.RunOnGameThread(() =>
    {
        IServerPlayer player = ServerHost.Server.GetClientByPlayername(PlayerName)?.Player
            ?? throw new InvalidOperationException("The loopback player is unavailable on the server.");
        ItemStack stack = player.InventoryManager.ActiveHotbarSlot.Itemstack
            ?? throw new InvalidOperationException("The server player has no active item stack.");
        return stack.Collectible.GetRemainingDurability(stack);
    });

    private async Task<BlockPos> PlaceFueledFirepitAsync()
    {
        BlockPos position = PlayerBlockPosition(2).UpCopy();

        ServerHost!.RunOnGameThread(() =>
        {
            ICoreServerAPI api = (ICoreServerAPI)ServerHost.Server.Api;
            Block block = api.World.GetBlock(new AssetLocation("game:firepit-extinct"))
                ?? throw new InvalidOperationException("The vanilla extinct firepit block is unavailable.");
            api.World.BlockAccessor.SetBlock(block.BlockId, position);

            BlockEntity firepit = api.World.BlockAccessor.GetBlockEntity(position)
                ?? throw new InvalidOperationException("The vanilla firepit block entity was not created.");
            PropertyInfo fuelSlotProperty = firepit.GetType().GetProperty(
                    "fuelSlot",
                    BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMemberException(firepit.GetType().FullName, "fuelSlot");
            ItemSlot fuelSlot = (ItemSlot)fuelSlotProperty.GetValue(firepit)!;
            Item firewood = api.World.GetItem(new AssetLocation("game:firewood"))
                ?? throw new InvalidOperationException("The vanilla firewood item is unavailable.");
            fuelSlot.Itemstack = new ItemStack(firewood, 4);
            fuelSlot.MarkDirty();
            firepit.MarkDirty(redrawOnClient: true);
        });

        bool synced = await StepUntilAsync(
            () => Client!.RunOnClientThread(() =>
                Client.Client.World.BlockAccessor.GetBlock(position).Code.Path == "firepit-extinct"),
            maxFrames: 120);
        Assert.True(synced, "The fueled firepit did not reach the client.");
        return position;
    }

    private BlockPos PlayerBlockPosition(int distance) => Client!.RunOnClientThread(() =>
    {
        EntityPos position = Client.Client.EntityPlayer?.Pos
            ?? throw new InvalidOperationException("The live client player has no position.");
        return new BlockPos(
            (int)Math.Floor(position.X) + distance,
            (int)Math.Floor(position.Y) - 1,
            (int)Math.Floor(position.Z));
    });

    private string ClientBlockCode(BlockPos position) => Client!.RunOnClientThread(() =>
        Client.Client.World.BlockAccessor.GetBlock(position).Code.ToString());

    private string ServerBlockCode(BlockPos position) => ServerHost!.RunOnGameThread(() =>
        ((ICoreServerAPI)ServerHost.Server.Api).World.BlockAccessor.GetBlock(position).Code.ToString());
}
