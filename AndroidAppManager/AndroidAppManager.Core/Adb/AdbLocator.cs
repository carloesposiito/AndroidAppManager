namespace AndroidAppManager.Core.Adb;

/// <summary>Finds adb.exe: explicit path, then app-bundled "adb" folder, then PATH.</summary>
public static class AdbLocator
{
    private const string ExeName = "adb.exe";

    public static string? Find(string? explicitPath, string appBaseDir, string? pathEnv)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        var bundled = Path.Combine(appBaseDir, "adb", ExeName);
        if (File.Exists(bundled))
            return bundled;

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
