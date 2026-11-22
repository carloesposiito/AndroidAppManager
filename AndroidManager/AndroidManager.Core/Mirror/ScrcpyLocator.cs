namespace AndroidManager.Core.Mirror;

/// <summary>Finds scrcpy.exe: explicit path, then the app-bundled "scrcpy" folder, then PATH.</summary>
public static class ScrcpyLocator
{
    private const string ExeName = "scrcpy.exe";

    /// <param name="extractedDir">Where the copy shipped inside the app is unpacked.</param>
    public static string? Find(string? explicitPath, string appBaseDir, string? pathEnv, string? extractedDir = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        var bundled = Path.Combine(appBaseDir, "scrcpy", ExeName);
        if (File.Exists(bundled))
            return bundled;

        if (!string.IsNullOrWhiteSpace(extractedDir) && File.Exists(Path.Combine(extractedDir, ExeName)))
            return Path.Combine(extractedDir, ExeName);

        foreach (var dir in (pathEnv ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), ExeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException) { /* invalid PATH entry */ }
        }
        return null;
    }
}
