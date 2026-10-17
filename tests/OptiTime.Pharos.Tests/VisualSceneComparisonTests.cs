using Zaldaryon.Pharos.Core;

namespace OptiTime.Pharos.Tests;

public sealed class VisualSceneComparisonTests
{
    private const int Width = 1280;
    private const int Height = 720;

    [Fact]
    public void MinimapDifferencesAreIgnoredButLargeWorldChangesExceedBudget()
    {
        byte[] referencePixels = CreateFrame(24, 48, 72, 255);
        byte[] actualPixels = (byte[])referencePixels.Clone();
        Fill(actualPixels, 1000, 0, 1275, 270, 220, 180, 80, 255);

        FramebufferComparisonResult minimapOnly = VisualSceneComparison.Compare(
            new FramebufferSnapshot(actualPixels, Width, Height),
            new FramebufferSnapshot(referencePixels, Width, Height));

        Assert.Equal(0, minimapOnly.DiffPixelCount);
        Assert.Equal((byte)220, actualPixels[(1000 * 4)]);
        Assert.Equal((byte)24, referencePixels[(1000 * 4)]);

        Fill(actualPixels, 200, 100, 900, 600, 0, 0, 0, 0);
        FramebufferComparisonResult missingWorldRegion = VisualSceneComparison.Compare(
            new FramebufferSnapshot(actualPixels, Width, Height),
            new FramebufferSnapshot(referencePixels, Width, Height));

        Assert.True(missingWorldRegion.DiffPixelCount > OptiTimeConfig.VisualDiffPixelBudget,
            $"The visual gate missed a large removed scene region ({missingWorldRegion.DiffPixelCount} pixels).");
    }

    private static byte[] CreateFrame(byte red, byte green, byte blue, byte alpha)
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = red;
            pixels[offset + 1] = green;
            pixels[offset + 2] = blue;
            pixels[offset + 3] = alpha;
        }

        return pixels;
    }

    private static void Fill(byte[] pixels, int left, int top, int right, int bottom, byte red, byte green, byte blue, byte alpha)
    {
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
        {
            int offset = (y * Width + x) * 4;
            pixels[offset] = red;
            pixels[offset + 1] = green;
            pixels[offset + 2] = blue;
            pixels[offset + 3] = alpha;
        }
    }
}
