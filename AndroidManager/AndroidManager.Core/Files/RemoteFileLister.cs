using AndroidManager.Core.Adb;

namespace AndroidManager.Core.Files;

public sealed record RemoteFile(string RelativePath, long Size, DateTimeOffset Modified);

public sealed record FolderSummary(string Folder, int FileCount, long TotalBytes);

/// <param name="Unreadable">Output lines that were not a file row and not "no such folder" (permission denied, ...).</param>
public sealed record RemoteListing(IReadOnlyList<RemoteFile> Files, int Unreadable);

/// <summary>Reads folders and files on the device's shared storage through the shell.</summary>
public sealed class RemoteFileLister(IAdbService adb)
{
    /// <summary>Folders up to two levels below /sdcard, so "DCIM" and "DCIM/Camera" are both choosable.</summary>
    public async Task<IReadOnlyList<string>> ListFoldersAsync(AdbEndpoint ep, CancellationToken ct = default)
    {
        var output = await adb.ShellAsync(ep, "find /sdcard/ -mindepth 1 -maxdepth 2 -type d", ct);
        var folders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Replace("//", "/");
            if (!line.StartsWith(RemotePath.Root + "/", StringComparison.Ordinal)) continue;
            var rel = line[(RemotePath.Root.Length + 1)..].Trim('/');
            if (!RemotePath.TryFolder(rel, out var folder)) continue;
            // Android/ holds other apps' data (unreadable, not photos); dot folders are caches.
            if (RemotePath.IsHidden(folder) || folder == "Android" || folder.StartsWith("Android/", StringComparison.Ordinal)) continue;
            folders.Add(folder);
        }
        return [.. folders];
    }

    /// <summary>Every visible file below <paramref name="folder"/>, recursively, with size and modification time.</summary>
    public async Task<RemoteListing> ListFilesAsync(AdbEndpoint ep, string folder, CancellationToken ct = default)
    {
        if (!RemotePath.TryFolder(folder, out var rel))
            throw new ArgumentException($"Cartella non valida: {folder}", nameof(folder));

        var prefix = RemotePath.Absolute(rel) + "/";
        var output = await adb.ShellAsync(ep,
            $"find {RemotePath.Quote(prefix)} -type f -exec stat -c '%s %Y %n' {{}} +", ct);

        var files = new List<RemoteFile>();
        var unreadable = 0;
        foreach (var raw in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseRow(raw, prefix, out var file))
            {
                if (!raw.Contains("No such file", StringComparison.OrdinalIgnoreCase)) unreadable++;
                continue;
            }
            if (!RemotePath.IsHidden(file!.RelativePath)) files.Add(file);
        }
        return new(files, unreadable);
    }

    public async Task<IReadOnlyList<FolderSummary>> SummarizeAsync(AdbEndpoint ep, IEnumerable<string> folders,
        CancellationToken ct = default)
    {
        var result = new List<FolderSummary>();
        foreach (var folder in folders)
        {
            var listing = await ListFilesAsync(ep, folder, ct);
            result.Add(new(folder, listing.Files.Count, listing.Files.Sum(f => f.Size)));
        }
        return result;
    }

    /// <summary>Row format: "size mtime path". The path is last because it may hold spaces.</summary>
    private static bool TryParseRow(string row, string prefix, out RemoteFile? file)
    {
        file = null;
        var parts = row.Split(' ', 3);
        if (parts.Length != 3 || !long.TryParse(parts[0], out var size) || !long.TryParse(parts[1], out var mtime)) return false;
        var path = parts[2].Replace("//", "/");
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rel = path[prefix.Length..];
        if (!RemotePath.IsSafeRelativeFile(rel)) return false;
        file = new(rel, size, DateTimeOffset.FromUnixTimeSeconds(mtime));
        return true;
    }
}
