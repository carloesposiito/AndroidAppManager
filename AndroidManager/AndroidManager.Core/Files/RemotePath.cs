namespace AndroidManager.Core.Files;

/// <summary>
/// Paths on the device's shared storage. Everything the app reads, writes or deletes goes through here:
/// folders are relative to /sdcard, never contain "..", and are always single-quoted for the shell.
/// </summary>
public static class RemotePath
{
    public const string Root = "/sdcard";

    /// <summary>Validates a folder relative to /sdcard and strips surrounding slashes.</summary>
    public static bool TryFolder(string? input, out string relative)
    {
        relative = "";
        var t = (input ?? "").Trim().Trim('/');
        if (t.Length == 0 || t.Any(char.IsControl) || t.Contains('\\')) return false;
        var parts = t.Split('/');
        if (parts.Any(p => p.Length == 0 || p == "." || p == "..")) return false;
        relative = t;
        return true;
    }

    /// <summary>A file path below a folder: relative, no "..", no control characters.</summary>
    public static bool IsSafeRelativeFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\') || path.Any(char.IsControl))
            return false;
        return path.Split('/').All(p => p.Length > 0 && p != "." && p != "..");
    }

    /// <summary>POSIX single-quote: safe against spaces, $, `, ; and embedded quotes.</summary>
    public static string Quote(string raw) => "'" + raw.Replace("'", "'\\''") + "'";

    public static string Absolute(string folder, string? file = null) =>
        file is null ? $"{Root}/{folder}" : $"{Root}/{folder}/{file}";

    /// <summary>Thumbnail caches, trash and half-written files start with a dot: not photos to move.</summary>
    public static bool IsHidden(string relativePath) => relativePath.Split('/').Any(p => p.StartsWith('.'));

    /// <summary>Selecting "DCIM" and "DCIM/Camera" must not move the camera files twice.</summary>
    public static IReadOnlyList<string> DropNested(IEnumerable<string> folders)
    {
        var set = folders.Distinct(StringComparer.Ordinal).ToList();
        return [.. set.Where(f => !set.Any(o => o != f && f.StartsWith(o + "/", StringComparison.Ordinal)))];
    }
}
