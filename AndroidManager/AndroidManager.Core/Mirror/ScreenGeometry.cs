using System.Text.RegularExpressions;

namespace AndroidManager.Core.Mirror;

/// <summary>The phone screen's proportions, so the mirror panel can hug the video with no side bars.</summary>
public static partial class ScreenGeometry
{
    /// <summary>One shell call: logical size plus current rotation.</summary>
    public const string Command = "wm size; dumpsys display | grep -m1 mCurrentOrientation";

    /// <summary>Screen size in pixels as the user sees it (rotation applied), or null when the output has no usable size.</summary>
    public static (int Width, int Height)? Parse(string output)
    {
        // "Override size" is what Android really uses when the user changed the resolution.
        var m = OverrideRx().Match(output);
        if (!m.Success) m = PhysicalRx().Match(output);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var w) || !int.TryParse(m.Groups[2].Value, out var h) || w <= 0 || h <= 0)
            return null;

        var rot = OrientationRx().Match(output);
        var landscape = rot.Success && int.Parse(rot.Groups[1].Value) % 2 == 1;
        return landscape ? (h, w) : (w, h);
    }

    /// <summary>Largest size with the ratio w:h that fits inside availW x availH. (0, 0) when there is no room.</summary>
    public static (double Width, double Height) Contain(double availW, double availH, int w, int h)
    {
        if (availW <= 0 || availH <= 0 || w <= 0 || h <= 0) return (0, 0);
        var scale = Math.Min(availW / w, availH / h);
        return (w * scale, h * scale);
    }

    [GeneratedRegex(@"Override size:\s*(\d+)x(\d+)")] private static partial Regex OverrideRx();
    [GeneratedRegex(@"Physical size:\s*(\d+)x(\d+)")] private static partial Regex PhysicalRx();
    [GeneratedRegex(@"mCurrentOrientation=(\d)")] private static partial Regex OrientationRx();
}
