using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vintagestory.API.Config;
using Vintagestory.API.Client;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Shared setup for every scenario: one engine-mode client joined to an
/// embedded server on a fixed seed, warmed up before anything is measured.
///
/// Engine mode is not optional here. It is the only mode that runs the mod
/// loader and the vanilla render pipeline, and the only mode where
/// <c>MeasureFramesAsync</c> works. Fixture mode never loads a mod at all, so
/// every OptiTime assertion would pass vacuously.
/// </summary>
public abstract class OptiTimeScenarioBase : ClientServerScenarioBase
{
    /// <summary>
    /// The frame at which measurement starts. Engine mode is real-time, not
    /// deterministic, so every sample is taken well past chunk streaming and
    /// shader compilation.
    /// </summary>
    protected override HeadlessClientOptions ClientOptions
    {
        get
        {
            // Pharos evaluates this after booting the embedded server and before
            // booting the client, so the UDP bridge is ready before any packet
            // can reach the patched client socket. During pool-key calculation
            // ServerHost is still null; a reused host already has its network
            // attached from the original boot.
            if (ServerHost is { } serverHost)
            {
                HarnessPatches.Attach(serverHost.UdpNetwork);
            }

            return new()
            {
                BootMode = ClientBootMode.Engine,
                Width = 1280,
                Height = 720,
            };
        }
    }

    /// <summary>
    /// Lets the harness fail a test on any error the client or server logged
    /// during it, rather than each test checking the log by hand. The log is
    /// cleared between tests, so "during it" means what it says.
    ///
    /// Turn this off in a class that deliberately provokes a log message; the
    /// log assertions in those classes are the point of the test.
    /// </summary>
    protected override bool FailOnLoggedErrors => true;

    /// <summary>
    /// Known defects in the mod under test, allowed through the harness' error
    /// gate so one bug does not turn the whole suite red and hide the state of
    /// everything else.
    ///
    /// Each entry is a client error the mod provokes on a path it controls, and
    /// each has a dedicated test that asserts it is still happening:
    /// <c>RegressionTests.HelpTextProducesNoRichTextParseErrors</c> turns the
    /// gate off and counts them directly. When the mod is fixed that test goes
    /// green and these entries become dead weight to remove in the same commit.
    ///
    /// Deliberately narrow - whole messages, not words. Widening this list to
    /// make a red suite green is the fastest way to lose the only signal the
    /// suite has.
    /// </summary>
    protected override IEnumerable<string> AllowedLoggedErrors =>
    [
        // OptiTimeConfig.GetOptimizationDescription returns English literals containing a
        // raw '>', and PrintOptStatus pastes them into ShowChatMessage with no escaping.
        // Every '.optitime status' spams the chat renderer. See finding 4.1.
        "Found closing tag char > but no tag was opened",
        "Found closing tag <font>",
    ];

    /// <summary>
    /// The mod's own configuration file inside this client's data path.
    ///
    /// Located by reflection on purpose. <c>DataBasePath</c> is declared on
    /// the API interface rather than on <c>ClientMain</c>, and the client and
    /// the test assembly end up with different views of that interface, so
    /// casting to reach it does not compile. Reading the property off the live
    /// object is both shorter and immune to which side declares it.
    /// </summary>
    protected string ModConfigPath => Path.Combine(ClientDataPath(), "ModConfig", "OptiTime.json");

    /// <summary>
    /// The data folder this client's mod API writes into.
    ///
    /// Read on the client thread: Pharos swaps the game-wide data path between
    /// the two sides of a loopback session, so the value is only correct while
    /// the client is the one being asked.
    /// </summary>
    protected string ClientDataPath()
    {
        object game = Client!.Client;

        string? path = Client.RunOnClientThread(() =>
        {
            Type type = game.GetType();

            foreach (string name in new[] { "DataBasePath", "DataPath" })
            {
                PropertyInfo? property = type.GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                    BindingFlags.FlattenHierarchy);

                if (property?.GetValue(game) is string value && value.Length > 0)
                {
                    return value;
                }
            }

            // Both names are declared on APIBase and are internal there, so a
            // public-only lookup finds neither. The game's own static is the
            // last resort, and is correct as long as the client is the side
            // being asked - which it is, inside RunOnClientThread.
            return GamePaths.DataPath;
        });

        return path ?? throw new InvalidOperationException(
            $"Neither DataBasePath nor DataPath is readable on {game.GetType().FullName}, and " +
            $"GamePaths.DataPath is empty. The suite cannot find the mod's config file. If " +
            "the mod's persistence tests start failing here, the client API moved that " +
            "property.");
    }

    protected Dictionary<string, string?> ReadRenderSettings()
    {
        Type? type = Client!.Client.GetType().Assembly.GetType("Vintagestory.Client.NoObf.ClientSettings");
        Assert.NotNull(type);
        var result = new Dictionary<string, string?>();
        foreach (string name in new[] { "ViewDistance", "ShadowMapQuality", "FieldOfView", "Vsync", "GuiScale", "MaxFps", "WavingFoliage", "CloudRenderMode" })
        {
            PropertyInfo? property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            result[name] = property?.GetValue(null)?.ToString() ?? "unavailable";
        }
        return result;
    }

    // ------------------------------------------------------------- diagnostics

    /// <summary>
    /// Runs frames until the client is out of startup work.
    ///
    /// The wait is on a real condition rather than a frame count because engine
    /// mode is not deterministic: a fixed count would sometimes measure while
    /// chunks were still streaming, which shows up as a huge allocation number
    /// and a slow P99 that has nothing to do with the mod.
    ///
    /// Whether the wait succeeded is deliberately not reported. A world that
    /// takes longer than the budget still boots and still renders; failing here
    /// would hide the measurements the caller came for. The chunk count is
    /// asserted separately, where a genuinely empty world is a real failure.
    /// </summary>
    protected async Task WarmUpAsync()
    {
        await Session!.StepFramesAsync(OptiTimeConfig.WarmupFrames);
        await StepUntilAsync(() => LoadedChunks() > 0, maxFrames: 900);
        Assert.True(((Zaldaryon.Pharos.Player.ClientTestPlayer)Player!).RawEntity!.Alive,
            "Profiling observer died before warm-up completed");

        await FreezeTimeOfDayAsync();
        ServerHost!.Weather.SetPattern("clearsky");
        ServerHost.Weather.SetWind("still");
        ServerHost.Weather.SetPrecipitation(-1);
        ServerHost.Weather.StopChanges();
        await Session.StepFramesAsync(OptiTimeConfig.PoseSettleFrames);
    }

    /// <summary>
    /// Stops the world's clock and pins it to midday.
    ///
    /// Not a convenience. The game clock keeps running while a scenario steps,
    /// and two framebuffer captures taken a few minutes apart are taken at
    /// different sun angles: the shadows move, the sky changes, and a
    /// "did this optimization change what is drawn" comparison reads a hundred
    /// thousand differing pixels for a reason that has nothing to do with the
    /// optimization under test. Pinning the clock is what makes every visual
    /// comparison in this suite a comparison of the optimization.
    ///
    /// The setting is on the embedded server and is not rolled back between
    /// tests, which is what makes it consistent across a class: the first test
    /// freezes it and every test after inherits it.
    /// </summary>
    protected async Task FreezeTimeOfDayAsync()
    {
        ServerHost!.Calendar.Freeze();
        ServerHost.Calendar.SetTime(OptiTimeConfig.FixedHourOfDay);
        await Session!.WaitForCalendarSyncAsync();
        Assert.True(ServerHost.Calendar.IsFrozen, "World calendar was not frozen");
    }

    /// <summary>
    /// How many chunks the client currently has. Used instead of a hard-coded
    /// accessor so a Pharos rename breaks this one line, not every test.
    /// </summary>
    protected int LoadedChunks() => Client!.Culling.Snapshot().TotalChunks;

    /// <summary>Requires the nearby world to be meshed and streaming to stabilize.</summary>
    protected async Task WaitForChunksToSettleAsync(int maxFrames = 1800)
    {
        // Global queues include work outside the inspected neighborhood.
        // Require actual nearby chunk readiness rather than global queue emptiness.
        await Session!.WaitForWorldReadyAsync(radius: 2, maxFrames: maxFrames);
        int previous = -1, stableFrames = 0;
        for (int frame = 0; frame < maxFrames; frame++)
        {
            await Session.StepFramesAsync(1);
            int loaded = LoadedChunks();
            stableFrames = loaded > 0 && loaded == previous ? stableFrames + 1 : 0;
            previous = loaded;
            if (stableFrames >= 30)
            {
                Console.WriteLine($"scene settled: loaded={loaded}, mesh={Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation}, upload={Vintagestory.Client.RuntimeStats.chunksAwaitingPooling}");
                return;
            }
        }
        Assert.Fail($"Chunk streaming did not settle within {maxFrames} frames");
    }

    /// <summary>
    /// Asserts the client logged nothing unexpected during this test.
    ///
    /// <c>Assert.Empty(Client.Logs.Errors)</c> is wrong in a suite that allows
    /// known defects: it re-introduces them as failures without saying which,
    /// and it fails a test that has nothing to do with them. This applies the
    /// same allowlist the harness' own gate applies, so a test that checks the
    /// log by hand and the harness that checks it for you agree on what
    /// "unexpected" means.
    /// </summary>
    protected void AssertNoUnexpectedClientErrors(string context)
    {
        string[] unexpected = [.. Client!.Logs.UnexpectedErrors(AllowedLoggedErrors)
            .Select(e => e.Message)];

        Assert.True(unexpected.Length == 0,
            $"{context} logged {unexpected.Length} unexpected client error(s):\n  " +
            string.Join("\n  ", unexpected.Take(10)));
    }

    // ------------------------------------------------------------------- gui

    /// <summary>
    /// Opens a dialog the way a player does: through the hotkey the dialog
    /// itself declares.
    ///
    /// The hotkey comes from <c>GuiDialog.ToggleKeyCombinationCode</c> rather
    /// than from a hand-written string, because those strings are not what they
    /// look like - the escape menu is <c>escapemenudialog</c>, the inventory is
    /// <c>inventorydialog</c>, and guessing produces
    /// <c>"No hotkey is registered as 'escape'"</c> from a test that looks like
    /// it is testing the mod.
    ///
    /// Triggering a hotkey also runs it on the client thread, which matters:
    /// opening a dialog directly composes it here, uploads Cairo textures from
    /// this thread, and the game throws <c>"Texture uploads must happen in the
    /// main thread. We only have one OpenGL context."</c> from inside
    /// <c>GuiComposer.Compose</c>, killing the scenario.
    ///
    /// Returns false when the dialog is not registered on this client, which is
    /// a legitimate outcome: play styles differ in which dialogs exist.
    /// </summary>
    protected async Task<bool> OpenDialogAsync(string dialogName, params string[] fallbackCodes)
    {
        string? code = Client!.RunOnClientThread(() =>
            Player!.Gui.LoadedDialogs
                .FirstOrDefault(d => d.GetType().Name == dialogName)
                ?.ToggleKeyCombinationCode);

        code ??= fallbackCodes.FirstOrDefault(c => Client.Hotkeys.Get(c) is not null);

        if (string.IsNullOrEmpty(code) || Client.Hotkeys.Get(code) is null)
        {
            return false;
        }

        await Client.Hotkeys.TriggerAsync(code);
        await Session!.StepFramesAsync(20);

        if (Client.Ui!.IsOpen(dialogName)) return true;

        // One retry with a longer settle. A dialog that is registered but has
        // not finished composing its first frame does not open on the first
        // key press, and treating that as "it does not open" would report a mod
        // defect that is really a race in the harness's own timing.
        await Client.Hotkeys.TriggerAsync(code);
        await Session!.StepFramesAsync(60);

        return Client.Ui!.IsOpen(dialogName);
    }

    /// <summary>
    /// Opens a dialog and asserts it came up, naming the dialog when it did
    /// not. A dialog that will not open is a real failure - every dialog
    /// transition goes through <c>GuiManager</c>, which the mod patches - and a
    /// silent false would turn every later assertion in the test into a
    /// vacuous pass.
    /// </summary>
    protected async Task OpenDialogRequiredAsync(string dialogName, params string[] fallbackCodes)
    {
        bool opened = await OpenDialogAsync(dialogName, fallbackCodes);

        Assert.True(opened,
            $"'{dialogName}' is registered on this client but would not open. Every dialog " +
            "transition goes through GuiManager, which is the class the mod patches.");
    }

    /// <summary>
    /// Opens the inventory dialog and confirms it came up.
    ///
    /// Asserts because several modules - the recipe lookup cache, the handbook
    /// cache, the GUI render path - are only reachable while it is on screen,
    /// and a test that could not open it would report "no effect" for all of
    /// them.
    /// </summary>
    protected async Task OpenInventoryAsync() =>
        await OpenDialogRequiredAsync("GuiDialogInventory", "inventorydialog");

    // ------------------------------------------------------- crafting grid

    /// <summary>
    /// Puts a real item into the player's crafting grid and waits for the grid
    /// to re-evaluate.
    ///
    /// <b>Not <c>InventoryAutomation.SetCraftingSlot</c>.</b> That helper
    /// cannot place an item in a live client - it writes to a private mock
    /// dictionary, or clears the slot and returns true without setting
    /// anything, because building an <c>ItemStack</c> needs the item registry.
    /// A test that used it would drive a crafting grid that is empty and then
    /// conclude the recipe cache was not being consulted.
    ///
    /// The stack is built by <c>ItemDriver.Stack</c>, which does resolve against
    /// the registry, and written straight into the grid's <c>ItemSlot</c> on the
    /// client thread. This is the one part of the harness the suite had to go
    /// around, and going around it is worth the ten lines: it is the only way to
    /// reach <c>InventoryCraftingGrid.FindMatchingRecipe</c>, which is where the
    /// recipe cache's prefix sits.
    /// </summary>
    protected async Task<bool> PlaceInCraftingGridAsync(int x, int y, params string[] itemCodes)
    {
        ItemStack stack = ResolveStack(itemCodes);
        var client = Client ?? throw new InvalidOperationException("Pharos did not provide a client for the crafting-grid scenario.");
        var player = Player ?? throw new InvalidOperationException("Pharos did not provide a player for the crafting-grid scenario.");
        var session = Session ?? throw new InvalidOperationException("Pharos did not provide a session for the crafting-grid scenario.");

        bool placed = client.RunOnClientThread(() =>
        {
            IInventory? crafting = player.Inventory.Crafting;
            if (crafting is null) return false;

            int index = (y * 3) + x;
            ItemSlot? slot = index < crafting.Count ? crafting[index] : null;
            if (slot is null) return false;

            slot.Itemstack = stack;
            return true;
        });

        await session.StepFramesAsync(20);
        return placed;
    }

    /// <summary>
    /// Builds the first of <paramref name="codes"/> the game's registry
    /// actually knows.
    ///
    /// The registry throws on an unknown code rather than returning null, so
    /// the candidates are tried in order and the failure is only reported once
    /// every one of them misses - at which point the reason is the useful part
    /// of the error.
    /// </summary>
    protected ItemStack ResolveStack(params string[] codes)
    {
        ArgumentException? last = null;

        foreach (string code in codes.Concat(SiblingVariants(codes.FirstOrDefault())))
        {
            try
            {
                return Client!.Items.Stack(code);
            }
            catch (ArgumentException ex)
            {
                last = ex;
            }
        }

        throw new ArgumentException(
            $"The game's registry knows none of: {string.Join(", ", codes)} " +
            $"or a sibling variant of the first.", nameof(codes), last);
    }

    /// <summary>
    /// Alternate variants of a code whose last segment names a material or a
    /// wood: <c>plank-oak</c> yields <c>plank-spruce</c> and the rest.
    ///
    /// Needed because the registry a headless client exposes is narrower than
    /// the asset tree: <c>plank-oak</c> resolves and <c>planks-oak</c> and
    /// <c>cobblestone</c> do not, with nothing in the assets to predict which.
    /// Deriving a sibling from something that <em>did</em> resolve is more
    /// robust than a longer hand-written list that will rot on the next update.
    /// </summary>
    protected static IEnumerable<string> SiblingVariants(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            yield break;
        }

        int dash = code.LastIndexOf('-');
        if (dash <= 0 || dash == code.Length - 1)
        {
            yield break;
        }

        string prefix = code[..dash];

        foreach (string variant in new[]
                 {
                     "spruce", "birch", "beech", "maple", "cherry", "oak",
                     "planks", "plank", "wood", "slab", "stone",
                 })
        {
            yield return $"{prefix}-{variant}";
        }
    }

    /// <summary>Same, but returns null instead of throwing.</summary>
    protected ItemStack? TryResolveStack(params string[] codes)
    {
        foreach (string code in codes)
        {
            try
            {
                return Client!.Items.Stack(code);
            }
            catch (ArgumentException)
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    /// <summary>Empties the crafting grid, on the client thread.</summary>
    protected async Task ClearCraftingGridAsync()
    {
        Client!.RunOnClientThread(() =>
        {
            IInventory? crafting = Player!.Inventory.Crafting;
            if (crafting is null) return true;

            for (int i = 0; i < crafting.Count; i++)
            {
                crafting[i]!.Itemstack = null;
            }

            return true;
        });

        await Session!.StepFramesAsync(10);
    }

    /// <summary>
    /// Closes a dialog.
    ///
    /// <c>GuiDriver.Close</c> marshals onto the client thread itself, so this
    /// is safe to call from the test thread. Opening a dialog directly is not:
    /// composing one uploads Cairo textures, and the game rejects that off the
    /// main thread with <c>"Texture uploads must happen in the main thread. We
    /// only have one OpenGL context."</c>, thrown from inside
    /// <c>GuiComposer.Compose</c> and fatal to the whole scenario.
    /// </summary>
    protected async Task CloseDialogAsync(string dialog)
    {
        Client!.Ui!.Close(dialog);
        await Session!.StepFramesAsync(10);
    }

    // ----------------------------------------------------------------- camera

    /// <summary>
    /// Moves the camera to a fixed pose and lets the world catch up.
    ///
    /// Both variants must land on exactly the same pose or the framebuffer
    /// comparison measures the camera instead of the mod. The settle frames
    /// matter: a pose change makes the client load new chunks, and capturing
    /// mid-stream produces a diff full of terrain that simply had not arrived.
    /// </summary>
    protected async Task<CameraPose> ApplyPoseAsync(CameraPose pose)
    {
        Assert.True(((Zaldaryon.Pharos.Player.ClientTestPlayer)Player!).RawEntity!.Alive,
            "Dead observer cannot provide a valid camera scene");
        var resolved = ServerHost!.RunOnGameThread(() =>
        {
            var server = ServerHost.Server;
            var spawn = server.DefaultSpawnPosition;
            double x = spawn.X + pose.Eye[0], z = spawn.Z + pose.Eye[2];
            double tx = spawn.X + pose.Target[0], tz = spawn.Z + pose.Target[2];
            double Surface(double sx, double sz)
            {
                var pos = new BlockPos((int)sx, 0, (int)sz);
                Assert.True(server.BlockAccessor.IsValidPos(pos), $"Camera column outside world: {pos}");
                Assert.NotNull(server.BlockAccessor.GetMapChunk(pos.X / 32, pos.Z / 32));
                return Math.Max(server.BlockAccessor.GetTerrainMapheightAt(pos),
                    server.BlockAccessor.GetRainMapHeightAt(pos.X, pos.Z));
            }
            double y = Surface(x, z) + pose.Eye[1];
            Assert.True(y < server.MapSize.Y - 2, "Camera clearance exceeds world height");
            double ty = pose.Name == "horizon" ? y : Surface(tx, tz) + pose.Target[1];
            return new CameraPose(pose.Name, [x, y, z], [tx, ty, tz]);
        });
        pose = resolved;
        long entityId = ((Zaldaryon.Pharos.Player.ClientTestPlayer)Player!).RawEntity!.EntityId;
        TaskCompletionSource teleported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ServerHost!.RunOnGameThread(() =>
        {
            var player = ServerHost.Server.PlayersByUid.Values.Single(p => p.Entity.EntityId == entityId);
            player.WorldData.FreeMove = true;
            player.WorldData.NoClip = true;
            player.BroadcastPlayerData();
            player.Entity.TeleportToDouble(pose.Eye[0], pose.Eye[1], pose.Eye[2],
                () => teleported.TrySetResult());
        });
        await StepUntilAsync(() => teleported.Task.IsCompleted, maxFrames: 900);
        Assert.True(teleported.Task.IsCompleted, "Server teleport did not complete while stepping the world");
        await Session!.StepFramesAsync(30);
        Player.Teleport(pose.Eye[0], pose.Eye[1], pose.Eye[2]);
        var expectedAngles = Client!.RunOnClientThread(() =>
        {
            var entity = ((Zaldaryon.Pharos.Player.ClientTestPlayer)Player).RawEntity!;
            var api = (ICoreClientAPI)entity.Api;
            var eye = entity.Pos.XYZ.Add(entity.LocalEyePos);
            double dx = pose.Target[0] - eye.X, dy = pose.Target[1] - eye.Y, dz = pose.Target[2] - eye.Z;
            float yaw = (float)GameMath.Mod(Math.Atan2(dx, dz), 2 * Math.PI);
            float pitch = GameMath.Clamp((float)(Math.PI - Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz))),
                1.5857964f, 4.697389f);
            entity.Pos.Yaw = api.Input.MouseYaw = yaw;
            entity.Pos.Pitch = api.Input.MousePitch = pitch;
            return (yaw, pitch);
        });
        await Session.StepFramesAsync(OptiTimeConfig.PoseSettleFrames);
        var actual = Player.Position;
        Assert.True(Math.Abs(actual.X - pose.Eye[0]) < 0.01
            && Math.Abs(actual.Y - pose.Eye[1]) < 0.01
            && Math.Abs(actual.Z - pose.Eye[2]) < 0.01,
            $"Camera pose rejected: requested={string.Join(',', pose.Eye)}, actual={actual}");
        var angles = Client!.RunOnClientThread(() =>
        {
            var pos = ((Zaldaryon.Pharos.Player.ClientTestPlayer)Player).RawEntity!.Pos;
            return (pos.Yaw, pos.Pitch);
        });
        Assert.True(Math.Abs(angles.Yaw - expectedAngles.yaw) < 0.001
            && Math.Abs(angles.Pitch - expectedAngles.pitch) < 0.001, "Camera orientation drifted");
        Console.WriteLine($"resolved pose {pose.Name}: eye={string.Join(',', pose.Eye)}, target={string.Join(',', pose.Target)}, yaw={angles.Yaw}, pitch={angles.Pitch}");
        return resolved;
    }

    internal static (double LitFraction, int Colors) RequireVisibleWorld(FramebufferSnapshot frame, string context)
    {
        int samples = 0, lit = 0;
        HashSet<int> colors = [];
        for (int y = frame.Height / 4; y < frame.Height * 3 / 4; y += 8)
        for (int x = frame.Width / 4; x < frame.Width * 3 / 4; x += 8)
        {
            var (r, g, b, _) = frame.GetPixel(x, y);
            samples++;
            if (Math.Max(r, Math.Max(g, b)) > 8) lit++;
            colors.Add((r << 16) | (g << 8) | b);
        }
        double fraction = lit / (double)samples;
        Assert.True(fraction >= 0.1 && colors.Count >= 32,
            $"{context}: world region is degenerate (lit={fraction:P2}, colors={colors.Count}); HUD is excluded");
        return (fraction, colors.Count);
    }

    // ---------------------------------------------------------------- command

    /// <summary>
    /// Runs a client command and returns the lines the client showed in chat.
    ///
    /// OptiTime reports everything through chat, so this is how the suite reads
    /// its state. It goes through the real command path, which means a command
    /// that throws fails the test - that is the point.
    /// </summary>
    protected async Task<CommandOutput> RunCommandAsync(string command)
    {
        ClientCommandResult result = await Client!.Commands.ExecuteAsync(command);

        // ChatLines carries the structured entry the client rendered; the text
        // a test can assert on is the message inside it.
        return new CommandOutput(
            command,
            result.Status.ToString(),
            result.Message,
            [.. result.ChatLines.Select(l => l.Message)]);
    }

    /// <summary>
    /// Runs a command and fails if the client rejected it.
    ///
    /// <c>EnumCommandStatus.UnknownLegacy</c> counts as success here, and it is
    /// not a loophole. Vintage Story's own legacy
    /// <c>ICoreClientAPI.RegisterCommand</c> overload - the one OptiTime uses -
    /// hard-codes that status on every invocation
    /// (<c>ChatCommandApi.RegisterCommand</c> returns
    /// <c>new TextCommandResult { Status = EnumCommandStatus.UnknownLegacy }</c>),
    /// so a mod using it can never report Success through this path. Requiring
    /// Success would mean the suite could not drive the mod's command at all;
    /// accepting UnknownLegacy while still rejecting Error keeps the check
    /// meaningful.
    /// </summary>
    protected async Task<CommandOutput> RunCommandOkAsync(string command)
    {
        ClientCommandResult result = await Client!.Commands.ExecuteAsync(command);

        if (result.Status == EnumCommandStatus.Error)
        {
            throw new InvalidOperationException(
                $"'{command}' was rejected by the client: {result.Message}");
        }

        return new CommandOutput(command, result.Status.ToString(), result.Message,
            [.. result.ChatLines.Select(line => line.Message)]);
    }

    // ------------------------------------------------------------------ state

    /// <summary>
    /// Reads the mod's full status output into a name-to-state map.
    ///
    /// Goes through the command rather than through the config file, because the
    /// command is what a player sees and what an automation tool would drive.
    /// If the two ever disagree - a flag that never reaches the command - the
    /// command is the one that is wrong in the eyes of the user.
    /// </summary>
    protected async Task<IReadOnlyDictionary<string, OptionState>> ReadStatusAsync()
    {
        CommandOutput status = await RunCommandOkAsync($"{OptiTimeConfig.Command} status");
        return OptiTimeConfig.ReadStatus(status.Text);
    }

    /// <summary>
    /// Flips one optimization and confirms the mod reports the state it now
    /// claims. A toggle that silently fails is worse than one that errors: the
    /// benchmark would go on to "measure" a module that was never applied.
    ///
    /// The check reads the token right after the option's colon rather than
    /// searching the whole line, because the description text routinely
    /// contains the word "off" and a substring search reads it backwards.
    /// </summary>
    protected async Task<OptionState> SetOptimizationAsync(string optimization, bool enabled)
    {
        string command = $"{OptiTimeConfig.Command} {optimization} {(enabled ? "on" : "off")}";
        await RunCommandOkAsync(command);
        await Session!.StepFramesAsync(5);

        OptionState? state = await ReadOptionStateAsync(optimization);
        if (state is null)
        {
            throw new InvalidOperationException(
                $"'{command}' reported success but '{optimization}' is absent from the status " +
                "output, so its state cannot be confirmed.");
        }

        using JsonDocument requested = JsonDocument.Parse(await File.ReadAllTextAsync(ModConfigPath));
        string property = OptiTimeConfig.Options[optimization];
        Assert.Equal(enabled, requested.RootElement.GetProperty(property).GetBoolean());

        bool expectedShadowSkip = optimization.Equals("shadowentity", StringComparison.OrdinalIgnoreCase)
            && !state.Value.Enabled
            && (state.Value.Conflict?.StartsWith("Shadow quality", StringComparison.Ordinal) ?? false)
            && Vintagestory.Client.NoObf.ClientSettings.ShadowMapQuality <= 1;
        if (expectedShadowSkip)
        {
            Console.WriteLine($"configuration {optimization}={enabled}, effective=OFF: {state.Value.Conflict}; not an active benchmark variant");
            return state.Value;
        }

        if (state.Value.Conflict is not null && enabled)
        {
            throw new InvalidOperationException(
                $"'{command}' reported success but the mod refuses to enable " +
                $"'{optimization}' because of a conflict: {state.Value.Conflict}");
        }

        if (enabled && !state.Value.Enabled)
        {
            throw new InvalidOperationException(
                $"'{command}' reported success but '{optimization}' still reads as OFF. " +
                "The module would be benchmarked in the wrong state.");
        }

        if (!enabled && state.Value.Enabled)
        {
            throw new InvalidOperationException(
                $"'{command}' reported success but '{optimization}' still reads as ON.");
        }

        return state.Value;
    }

    /// <summary>One option's state, straight from the mod's status output.</summary>
    protected async Task<OptionState?> ReadOptionStateAsync(string optimization)
    {
        CommandOutput status = await RunCommandOkAsync($"{OptiTimeConfig.Command} {optimization}");
        return OptiTimeConfig.ReadOptionState(status.Text, optimization);
    }

    /// <summary>
    /// Reads every option's current state, so a test that changes several can
    /// put all of them back.
    /// </summary>
    protected async Task<IReadOnlyDictionary<string, OptionState>> SnapshotStatusAsync() =>
        await ReadStatusAsync();

    /// <summary>
    /// Puts every option back to the state it had before, in one pass.
    ///
    /// Toggling writes straight through to the mod's config file inside the
    /// client's data folder, and Pharos' rollback restores the world and the
    /// connection but not that file. A test that leaves an optimization off
    /// would therefore silently change what every later class measures. The
    /// only safe place to change an option is inside this helper.
    /// </summary>
    protected async Task RestoreStatusAsync(IReadOnlyDictionary<string, OptionState> before)
    {
        foreach ((string option, OptionState state) in before)
        {
            // An option the mod refuses to enable because of a conflict is left
            // exactly as it was; trying to flip it only produces a chat message
            // the next test then reads as noise.
            if (state.Conflict is not null && !state.Enabled) continue;

            await SetOptimizationAsync(option, state.Enabled);
        }
    }

    /// <summary>
    /// Runs a scenario after remembering one optimization's saved setting, then
    /// restores that setting even if an assertion or interaction fails.
    /// </summary>
    protected async Task WithOptimizationStateRestoredAsync(string optimization, Func<Task> body)
    {
        OptionState before = await ReadOptionStateAsync(optimization)
            ?? throw new InvalidOperationException($"'{optimization}' is absent from status output.");

        try
        {
            await body();
        }
        finally
        {
            if (before.Conflict is null || before.Enabled)
            {
                await SetOptimizationAsync(optimization, before.Enabled);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> with one optimization in a given state and
    /// puts it back afterwards, whatever happens.
    ///
    /// Every toggle in this suite goes through here. The restore is in a
    /// <c>finally</c> on purpose: an assertion failure inside the body is the
    /// most likely moment for the option to be left wrong, and that is exactly
    /// the moment the next test would not notice.
    /// </summary>
    protected async Task WithOptimizationAsync(string option, bool enabled, Func<Task> body)
    {
        OptionState before = await SetOptimizationAsync(option, false);
        try
        {
            await SetOptimizationAsync(option, enabled);
            await body();
        }
        finally
        {
            await SetOptimizationAsync(option, before.Enabled);
        }
    }

    // ------------------------------------------------------------------- diag

    /// <summary>
    /// Turns the mod's diagnostic counters on and clears whatever they counted
    /// before, so the next dump describes this test and not the previous one.
    ///
    /// The counters are process-wide statics. Under the default rollback
    /// isolation the client is shared across every test in the class, so
    /// without the reset a second test would read the first test's numbers and
    /// pass for the wrong reason.
    /// </summary>
    protected async Task ResetDiagAsync(string module)
    {
        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag {module} on");
        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag {module} reset");
    }

    /// <summary>
    /// Runs the module's code for a real window and reads its counters back.
    ///
    /// Returns null when the module produced no dump at all, which is a
    /// different failure from every counter reading zero and is reported
    /// differently by every test that calls this.
    /// </summary>
    protected async Task<DiagCounters> CaptureDiagAsync(
        string module,
        int frames = OptiTimeConfig.DiagWindowFrames)
    {
        await ResetDiagAsync(module);
        await Session!.StepFramesAsync(frames);
        await RunCommandAsync($"{OptiTimeConfig.Command} diag {module} dump");

        // The dump goes out through Logger.Notification as well as chat, and the
        // log copy is not HTML-escaped, so it is the reliable one to parse.
        return DiagReader.Parse(DiagReader.Body(ClientLogText(), module));
    }

    /// <summary>Everything the client has logged, newline joined.</summary>
    protected string ClientLogText() =>
        string.Join('\n', Client!.Logs.Entries.Select(e => e.Message));

    // ------------------------------------------------------------ measurement

    /// <summary>
    /// Measures a window of frames and returns a flat record.
    ///
    /// Everything downstream reads this record instead of <c>FrameMeasurement</c>,
    /// so the exact shape of Pharos' measurement type is isolated to this one
    /// method. If a field moves or changes type, this is the only method that
    /// needs fixing.
    /// </summary>
    protected async Task<Sample> MeasureAsync(string label, int frames = OptiTimeConfig.MeasureFrames)
    {
        FrameMeasurement m = await Session!.MeasureFramesAsync(frames);

        return new Sample(
            label,
            frames,
            m.Median,
            m.P95,
            m.P99,
            m.Work.Mean,
            m.Work.Min,
            m.Work.Max,
            m.AllocatedBytesPerFrame,
            m.ProcessAllocatedBytes / Math.Max(1, frames),
            m.Gen0Collections,
            m.Gen1Collections,
            m.Gen2Collections);
    }

    // ----------------------------------------------------------------- output

    /// <summary>
    /// Appends one measurement to the run report. Reports accumulate across
    /// tests, so a single failing run still leaves the numbers that led up to
    /// the failure on disk.
    /// </summary>
    protected static void Record(Sample sample, string scenario)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, OptiTimeConfig.VariantArtifacts, "measurements");
        Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, $"{scenario}.jsonl");
        string line = JsonSerializer.Serialize(
            new
            {
                scenario,
                game = OptiTimeConfig.TestedGameVersion,
                mod = OptiTimeConfig.ModVersion,
                sample,
            },
            ReportJson);

        File.AppendAllText(path, line + Environment.NewLine);
    }

    /// <summary>
    /// Writes a free-form artifact - a diag dump, a status listing, a log - so
    /// a failure leaves something a human can read instead of only a message.
    /// </summary>
    protected static Task WriteArtifactAsync(string scenario, string name, string content)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, OptiTimeConfig.VariantArtifacts, scenario);
        Directory.CreateDirectory(dir);
        return File.WriteAllTextAsync(Path.Combine(dir, name), content);
    }

    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>What a client command reported.</summary>
public readonly record struct CommandOutput(
    string Command,
    string Status,
    string? Message,
    IReadOnlyList<string> ChatLines)
{
    /// <summary>
    /// Everything the client showed, joined. OptiTime spreads its status over
    /// several chat lines, so matching against any single one is unreliable.
    /// </summary>
    public string Text => string.Join('\n', ChatLines is { Count: > 0 } ? ChatLines : [Message ?? ""]);
}

/// <summary>
/// One measurement window, flattened. Times are wall-clock on the client
/// thread only: worker threads (chunk tessellation, network, async particles)
/// and the GPU are deliberately excluded, because a renderer is charged for
/// submitting work rather than for the GPU finishing it.
/// </summary>
public sealed record Sample(
    string Label,
    int Frames,
    TimeSpan Median,
    TimeSpan P95,
    TimeSpan P99,
    TimeSpan Mean,
    TimeSpan Min,
    TimeSpan Max,
    double AllocatedBytesPerFrame,
    long ProcessAllocatedBytesPerFrame,
    int Gen0,
    int Gen1,
    int Gen2)
{
    /// <summary>
    /// Median frame time as FPS. Reported alongside the time because "4% faster"
    /// and "4% lower FPS" are very different numbers at 20fps and at 144fps.
    /// </summary>
    public double MedianFps => Median.TotalSeconds <= 0 ? 0 : 1.0 / Median.TotalSeconds;

    public double P95Fps => P95.TotalSeconds <= 0 ? 0 : 1.0 / P95.TotalSeconds;
}
