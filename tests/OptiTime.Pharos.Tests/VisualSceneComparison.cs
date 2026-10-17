using Zaldaryon.Pharos.Core;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Compares the rendered world while excluding the minimap, whose revealed
/// map texture varies between clean client profiles even on the same save.
/// </summary>
internal static class VisualSceneComparison
{
    private const int MinimapLeft = 1000;
    private const int MinimapTop = 0;
    private const int MinimapRight = 1275;
    private const int MinimapBottom = 270;

    public static FramebufferComparisonResult Compare(FramebufferSnapshot actual, FramebufferSnapshot reference)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(reference);

        if (actual.Width != reference.Width || actual.Height != reference.Height)
            throw new ArgumentException("Visual captures must have matching dimensions.", nameof(reference));

        byte[] pixels = MaskMinimap(actual.RawRgba, actual.Width, actual.Height);
        byte[] referencePixels = MaskMinimap(reference.RawRgba, reference.Width, reference.Height);
        return new FramebufferSnapshot(pixels, actual.Width, actual.Height).Compare(referencePixels, 0.02f);
    }

    public static byte[] MaskMinimap(byte[] pixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != checked(width * height * 4))
            throw new ArgumentException("Pixel data length does not match the image dimensions.", nameof(pixels));

        byte[] masked = (byte[])pixels.Clone();
        int left = Math.Min(MinimapLeft, width);
        int right = Math.Min(MinimapRight, width);
        int top = Math.Min(MinimapTop, height);
        int bottom = Math.Min(MinimapBottom, height);
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
        {
            int offset = (y * width + x) * 4;
            Array.Clear(masked, offset, 4);
        }

        return masked;
    }
}
