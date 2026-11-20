namespace AndroidAppManager.Core.Tests.Support;

internal static class TestAssets
{
    /// <summary>Real APK from Extra/APK. Throws when absent so a missing fixture fails loudly instead of passing vacuously.</summary>
    public static string Apk(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "Extra", "APK", name);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException($"Fixture mancante: Extra/APK/{name}");
    }

    public const string Launcher = "xbmc-launcher-3-4-3.apk";
    public const string Kodi = "kodi-21.3-Omega-arm64-v8a.apk";
    public const string TheWorld = "TheWorldXC_Black_v7.apk";
}
