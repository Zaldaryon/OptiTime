using System.Reflection;
using System.Text.Json;

namespace OptiTime.Pharos.Tests;

public abstract class VisualControlBase : RenderingFidelityBase
{
    protected async Task CaptureControlAsync(string variant, bool compareFreshVanilla)
    {
        await WarmUpAsync();
        string directory = Path.Combine(AppContext.BaseDirectory, "artifacts", "visual-control");
        Directory.CreateDirectory(directory);
        var observations = new List<object>();
        var mismatches = new List<string>();

        foreach (CameraPose pose in OptiTimeConfig.Poses)
        {
            CameraPose resolved = await CaptureAtAsync(pose);
            Vec3d firstPosition = Player!.Position.Clone();
            FramebufferSnapshot first = Client!.CaptureFrame();
            first.SaveToPng(Path.Combine(directory, $"{variant}-{pose.Name}.png"));
            await CaptureAtAsync(pose);
            Vec3d secondPosition = Player.Position.Clone();
            FramebufferSnapshot second = Client.CaptureFrame();
            second.SaveToPng(Path.Combine(directory, $"{variant}-{pose.Name}-repeat.png"));
            FramebufferComparisonResult churn = first.Compare(second, tolerance: 0.02f);
            string originalPath = BaselinePath(pose, "vanilla");
            int? oldBaselinePixels = null;
            double? oldBaselineMean = null;
            if (File.Exists(originalPath))
            {
                FramebufferComparisonResult original = first.Compare(
                    FramebufferSnapshot.FromFile(originalPath), tolerance: 0.02f);
                oldBaselinePixels = original.DiffPixelCount;
                oldBaselineMean = original.MeanDiff;
            }
            FramebufferComparisonResult? fresh = null;
            if (compareFreshVanilla)
            {
                string? referenceDirectory = Environment.GetEnvironmentVariable("OPTITIME_PHAROS_REFERENCE");
                Assert.False(string.IsNullOrEmpty(referenceDirectory), "Fresh vanilla reference must come from this execution.");
                string reference = Path.Combine(referenceDirectory!, $"fresh-vanilla-{pose.Name}.png");
                Assert.True(File.Exists(reference), "Run FreshVanillaVisualControl first to create this run's vanilla reference.");
                using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(referenceDirectory!, "fresh-vanilla.json")));
                var control = metadata.RootElement.EnumerateArray().Single(item => item.GetProperty("pose").GetString() == pose.Name);
                Assert.Equal(JsonSerializer.Serialize(resolved.Eye), JsonSerializer.Serialize(control.GetProperty("requestedPosition")));
                Assert.Equal(JsonSerializer.Serialize(resolved.Target), JsonSerializer.Serialize(control.GetProperty("target")));
                Assert.Equal(JsonSerializer.Serialize(Client.RunOnClientThread(ReadRenderSettings)), JsonSerializer.Serialize(control.GetProperty("settings")));
                Assert.Equal(JsonSerializer.Serialize(ReadVisualAmbient()), JsonSerializer.Serialize(control.GetProperty("ambient")));
                Assert.True(churn.DiffPixelCount <= OptiTimeConfig.VisualDiffPixelBudget, $"{pose.Name}: mod repeat scene is unstable ({churn.DiffPixelCount} pixels)");
                Assert.True(control.GetProperty("repeatPixels").GetInt32() <= OptiTimeConfig.VisualDiffPixelBudget, $"{pose.Name}: vanilla repeat scene is unstable");
                fresh = first.Compare(FramebufferSnapshot.FromFile(reference), tolerance: 0.02f);
                if (fresh.Value.DiffPixelCount > OptiTimeConfig.VisualDiffPixelBudget)
                    mismatches.Add($"{pose.Name}: fresh vanilla/mod delta {fresh.Value.DiffPixelCount} pixels; repeat churn {churn.DiffPixelCount}");
            }
            observations.Add(new
            {
                pose = pose.Name,
                requestedPosition = resolved.Eye,
                target = resolved.Target,
                actualPosition = new[] { firstPosition.X, firstPosition.Y, firstPosition.Z },
                repeatPosition = new[] { secondPosition.X, secondPosition.Y, secondPosition.Z },
                chunks = LoadedChunks(),
                first.Width,
                first.Height,
                oldBaselinePixels,
                oldBaselineMean,
                repeatPixels = churn.DiffPixelCount,
                freshVanillaPixels = fresh?.DiffPixelCount,
                settings = Client.RunOnClientThread(ReadRenderSettings),
                ambient = ReadVisualAmbient(),
            });
            string oldBaselineLabel = oldBaselinePixels?.ToString() ?? "unavailable";
            Console.WriteLine($"{variant}/{pose.Name}: old={oldBaselineLabel}, repeat={churn.DiffPixelCount}, fresh={fresh?.DiffPixelCount}, requested={string.Join(',', resolved.Eye)}, actual={firstPosition}, repeatPosition={secondPosition}");
        }
        File.WriteAllText(Path.Combine(directory, variant + ".json"),
            JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.WriteAllText(Path.Combine(directory, variant + "-client.log"), ClientLogText());
        if (compareFreshVanilla)
            Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }


}

[ServerWorld(seed: OptiTimeConfig.WorldSeed, playStyle: OptiTimeConfig.PlayStyle, worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[ClientSetting("wavingStuff", false)]
public class FreshVanillaVisualControl : VisualControlBase
{
    [ClientServerScenario(TimeoutMs = 900_000)]
    public Task CaptureUnmodifiedVanillaControl() => CaptureControlAsync("fresh-vanilla", compareFreshVanilla: false);
}

[ServerWorld(seed: OptiTimeConfig.WorldSeed, playStyle: OptiTimeConfig.PlayStyle, worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[ClientSetting("wavingStuff", false)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class FreshModVisualControl : VisualControlBase
{
    [ClientServerScenario(TimeoutMs = 900_000)]
    public Task CompareModWithFreshVanillaControl() => CaptureControlAsync("fresh-mod", compareFreshVanilla: true);
}
