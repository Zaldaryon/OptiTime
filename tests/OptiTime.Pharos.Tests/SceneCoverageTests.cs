namespace OptiTime.Pharos.Tests;

public class SceneCoverageTests
{
    [Fact]
    public void BlackWorldIsRejected() => Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
        OptiTimeScenarioBase.RequireVisibleWorld(Frame((x, y) => (0, 0, 0)), "black"));

    [Fact]
    public void ColorfulHudCannotCertifyBlackWorld() => Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
        OptiTimeScenarioBase.RequireVisibleWorld(Frame((x, y) => y < 64 || y >= 192
            ? ((byte)x, (byte)y, (byte)(x + y)) : ((byte)0, (byte)0, (byte)0)), "hud-only"));

    [Fact]
    public void UniformSkyIsRejected() => Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
        OptiTimeScenarioBase.RequireVisibleWorld(Frame((x, y) => (80, 120, 200)), "uniform"));

    [Fact]
    public void VariedVisibleWorldIsAccepted()
    {
        var result = OptiTimeScenarioBase.RequireVisibleWorld(
            Frame((x, y) => ((byte)x, (byte)y, (byte)(x + y))), "visible");
        Assert.Equal(1, result.LitFraction);
        Assert.True(result.Colors >= 32);
    }

    private static FramebufferSnapshot Frame(System.Func<int, int, (byte R, byte G, byte B)> color)
    {
        byte[] pixels = new byte[256 * 256 * 4];
        for (int y = 0; y < 256; y++)
        for (int x = 0; x < 256; x++)
        {
            var (r, g, b) = color(x, y);
            int offset = (y * 256 + x) * 4;
            pixels[offset] = r; pixels[offset + 1] = g; pixels[offset + 2] = b; pixels[offset + 3] = 255;
        }
        return new FramebufferSnapshot(pixels, 256, 256);
    }
}
