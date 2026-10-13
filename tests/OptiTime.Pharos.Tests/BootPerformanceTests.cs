using System.Security.Cryptography;
using System.Text.Json;
using Zaldaryon.Pharos.Reporting;

namespace OptiTime.Pharos.Tests;

public abstract class BootPerformanceBase : OptiTimeScenarioBase
{
    protected async Task MeasureBootVariantAsync(string variant, bool? requestedEnabled)
    {
        await WarmUpAsync();
        CameraPose pose = await ApplyPoseAsync(OptiTimeConfig.Poses[0]);
        await WaitForChunksToSettleAsync();
        await Session!.StepFramesAsync(600);
        await WaitForChunksToSettleAsync();
        var settings = Client!.RunOnClientThread(ReadRenderSettings);
        string run = Guid.NewGuid().ToString("N");
        string dir = Path.Combine(AppContext.BaseDirectory, "artifacts", "boot-performance", run);
        Directory.CreateDirectory(dir);
        var scene = Client!.CaptureFrame();
        scene.SaveToPng(Path.Combine(dir, "scene.png"));
        var coverage = RequireVisibleWorld(scene, "boot-" + variant);
        string? config = requestedEnabled.HasValue ? await File.ReadAllTextAsync(ModConfigPath) : null;
        if (config != null)
        {
            using JsonDocument parsed = JsonDocument.Parse(config);
            foreach (string option in OptiTimeConfig.OptionNames.Except(OptiTimeConfig.NonOptimizations))
            {
                // Engine can legitimately reject activation at boot. Preserve and report
                // resulting effective configuration rather than forcing fake activation.
                bool stored = parsed.RootElement.GetProperty(OptiTimeConfig.Options[option]).GetBoolean();
                if (!requestedEnabled!.Value) Assert.False(stored, $"{option} was enabled in disabled boot variant");
            }
            await File.WriteAllTextAsync(Path.Combine(dir, "config-after-boot.json"), config + "\n");
        }

        List<object> samples = [];
        for (int sample = 0; sample < 5; sample++)
        {
            var before = Player!.Position.Clone();
            double hoursBefore = Client!.RunOnClientThread(() => Client.Client.Calendar.TotalHours);
            var queuesBefore = (Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation, Vintagestory.Client.RuntimeStats.chunksAwaitingPooling);
            FrameMeasurement measured = await Session!.MeasureFramesAsync(600);
            var after = Player.Position.Clone();
            double hoursAfter = Client!.RunOnClientThread(() => Client.Client.Calendar.TotalHours);
            Assert.Equal(hoursBefore, hoursAfter);
            Assert.Equal(before.X, after.X);
            Assert.Equal(before.Y, after.Y);
            Assert.Equal(before.Z, after.Z);
            Assert.Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(Client.RunOnClientThread(ReadRenderSettings)));
            Assert.True(ServerHost!.Calendar.IsFrozen, "Calendar changed during timing");
            samples.Add(new
            {
                sample,
                hoursBefore,
                hoursAfter,
                meshBefore = queuesBefore.Item1,
                uploadBefore = queuesBefore.Item2,
                meshAfter = Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation,
                uploadAfter = Vintagestory.Client.RuntimeStats.chunksAwaitingPooling,
                measured.Frames,
                medianMs = measured.Median.TotalMilliseconds,
                p95Ms = measured.P95.TotalMilliseconds,
                p99Ms = measured.P99.TotalMilliseconds,
                workMeanMs = measured.Work.Mean.TotalMilliseconds,
                measured.AllocatedBytesPerFrame,
                processAllocatedBytesPerFrame = measured.ProcessAllocatedBytes / Math.Max(1, measured.Frames),
                measured.Gen0Collections,
                measured.Gen1Collections,
                measured.Gen2Collections,
                positionBefore = new[] { before.X, before.Y, before.Z },
                positionAfter = new[] { after.X, after.Y, after.Z },
                stages = measured.Stages.Select(stage => new
                {
                    name = stage.Stage.ToString(),
                    meanMs = stage.PerFrame.Mean.TotalMilliseconds,
                    stage.Calls,
                    stage.FramesRun
                }).ToArray()
            });
            Assert.NotEmpty(measured.Stages);
        }
        var result = new
        {
            run,
            variant,
            requestedEnabled,
            seed = OptiTimeConfig.WorldSeed,
            settings,
            playStyle = OptiTimeConfig.PlayStyle,
            worldType = OptiTimeConfig.WorldType,
            modPackageHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, OptiTimeConfig.ModAssembly)))),
            framesPerSample = 600,
            pose,
            worldLitFraction = coverage.LitFraction,
            worldColors = coverage.Colors,
            configHash = config == null ? null : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(config))),
            measurement = "Pharos client-thread elapsed work; includes submission/waits, not native-platform FPS, GPU duration or CPU utilization",
            bootIsolation = "Run one variant per dotnet test process; never combine these classes in one invocation",
            samples
        };
        await File.WriteAllTextAsync(Path.Combine(dir, "measurements.json"), JsonSerializer.Serialize(result) + "\n");
        if (config != null)
            await WriteArtifactAsync("boot-performance", $"{run}-status.txt",
                (await RunCommandAsync($"{OptiTimeConfig.Command} status")).Text);
        await File.WriteAllTextAsync(Path.Combine(dir, "client.log"), ClientLogText());
        Console.WriteLine($"{variant}: render-step measurements written to {dir}");
        AssertNoUnexpectedClientErrors($"boot variant {variant}");
    }
}

[ServerWorld(seed: OptiTimeConfig.WorldSeed, playStyle: OptiTimeConfig.PlayStyle, worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
public class BootVanillaPerformanceTests : BootPerformanceBase
{
    [ClientServerScenario(TimeoutMs = 900_000)]
    public Task MeasureVanillaAtBoot() => MeasureBootVariantAsync("vanilla", null);
}

[ServerWorld(seed: OptiTimeConfig.WorldSeed, playStyle: OptiTimeConfig.PlayStyle, worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
[DataFiles("Fixtures/AllDisabled.json", To = "ModConfig/OptiTime.json", Side = EnumAppSide.Client)]
public class BootDisabledPerformanceTests : BootPerformanceBase
{
    [ClientServerScenario(TimeoutMs = 900_000)]
    public Task MeasureDisabledAtBoot() => MeasureBootVariantAsync("mod-disabled-before-boot", false);
}

[ServerWorld(seed: OptiTimeConfig.WorldSeed, playStyle: OptiTimeConfig.PlayStyle, worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
[DataFiles("Fixtures/AllEnabled.json", To = "ModConfig/OptiTime.json", Side = EnumAppSide.Client)]
public class BootEnabledPerformanceTests : BootPerformanceBase
{
    [ClientServerScenario(TimeoutMs = 900_000)]
    public Task MeasureEnabledAtBoot() => MeasureBootVariantAsync("mod-enabled-before-boot", true);
}
