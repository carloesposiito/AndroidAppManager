using System.Globalization;

namespace AndroidManager.App;

internal static class Fmt
{
    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>1536 -> "1,5 KB".</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : string.Create(It, $"{v:0.#} {units[u]}");
    }
}
