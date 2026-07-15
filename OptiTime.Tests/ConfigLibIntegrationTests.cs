using Vintagestory.API.Datastructures;
using Xunit;

namespace OptiTime.Tests;

public class ConfigLibIntegrationTests
{
    [Fact]
    public void AppliesAllNumericSettingsAndReturnsNumericFeedback()
    {
        OptiTimeConfig config = new();

        AssertNumericSetting(config, "BackgroundMaxFps", 35, "35");
        AssertNumericSetting(config, "PreciseFramePacingUndershootPercent", 0.075f, "0.075");
        AssertNumericSetting(config, "PreciseFramePacingYieldThresholdMs", 0.75f, "0.75");
        AssertNumericSetting(config, "PreciseFramePacingSpinIterations", 64, "64");

        Assert.Equal(35, config.BackgroundMaxFps);
        Assert.Equal(0.075, config.PreciseFramePacingUndershootPercent);
        Assert.Equal(0.75, config.PreciseFramePacingYieldThresholdMs);
        Assert.Equal(64, config.PreciseFramePacingSpinIterations);
    }

    private static void AssertNumericSetting(OptiTimeConfig config, string code, int value, string expectedFeedback)
    {
        TreeAttribute tree = new();
        tree.SetInt("value", value);

        Assert.True(ConfigLibIntegration.TryApplySetting(config, code, tree, out string feedback));
        Assert.Equal(expectedFeedback, feedback);
    }

    private static void AssertNumericSetting(OptiTimeConfig config, string code, float value, string expectedFeedback)
    {
        TreeAttribute tree = new();
        tree.SetFloat("value", value);

        Assert.True(ConfigLibIntegration.TryApplySetting(config, code, tree, out string feedback));
        Assert.Equal(expectedFeedback, feedback);
    }
}
