using System.IO.Compression;

namespace AndroidManager.Core.Mirror;

/// <summary>Unpacks the scrcpy release zip shipped inside the app.</summary>
public static class ScrcpyBundle
{
    /// <summary>
    /// Extracts the archive into <paramref name="destDir"/>, dropping a single top-level folder
    /// (release zips wrap everything in "scrcpy-win64-vX/"). Returns the path of scrcpy.exe.
    /// Throws <see cref="InvalidDataException"/> for an entry that would land outside the folder or a zip without scrcpy.exe.
    /// </summary>
    public static string Extract(Stream zip, string destDir)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        var files = archive.Entries.Where(e => e.Name.Length > 0).ToList();

        var roots = files.Select(e => e.FullName.Replace('\\', '/').Split('/')[0]).Distinct().ToList();
        var strip = roots.Count == 1 && files.All(e => e.FullName.Replace('\\', '/').Contains('/'));

        var root = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // Validate every path before writing anything.
        var plan = new List<(ZipArchiveEntry Entry, string Target)>();
        foreach (var e in files)
        {
            var rel = e.FullName.Replace('\\', '/');
            if (strip) rel = rel[(rel.IndexOf('/') + 1)..];
            var target = Path.GetFullPath(Path.Combine(root, rel));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Voce non valida nello zip: {e.FullName}");
            plan.Add((e, target));
        }

        var exe = Path.Combine(root, "scrcpy.exe");
        if (!plan.Any(p => string.Equals(p.Target, exe, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Lo zip non contiene scrcpy.exe.");

        foreach (var (entry, target) in plan)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
        return exe;
    }
}
