using AndroidManager.Core.Adb;

namespace AndroidManager.Core.Files;

/// <param name="Fraction">0..1 progress of the current file.</param>
public sealed record FileProgress(int Done, int Total, string Current, double Fraction = 0);

public sealed record BulkResult(int Total, int Copied, IReadOnlyList<string> Errors)
{
    public int Failed => Errors.Count;
}

/// <summary>One-off device actions of the Files tab: folder to PC, files to phone, screenshot, reboot.</summary>
public sealed class DeviceTools(IAdbService adb)
{
    private sealed class Relay(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }

    /// <summary>Copies a whole device folder (recursively) into an empty local folder. Nothing is deleted anywhere.</summary>
    public async Task<BulkResult> CopyFolderToPcAsync(AdbEndpoint ep, string folder, string destDir,
        IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        if (!RemotePath.TryFolder(folder, out var rel))
            throw new ArgumentException($"Cartella non valida: {folder}", nameof(folder));
        if (Directory.Exists(destDir) && Directory.EnumerateFileSystemEntries(destDir).Any())
            throw new InvalidOperationException("La cartella di destinazione non è vuota.");

        var listing = await new RemoteFileLister(adb).ListFilesAsync(ep, rel, ct);
        Directory.CreateDirectory(destDir);
        var errors = new List<string>();
        if (listing.Unreadable > 0) errors.Add($"{listing.Unreadable} elementi non leggibili sul dispositivo.");

        var copied = 0;
        for (var i = 0; i < listing.Files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = listing.Files[i];
            var local = UniqueLocalPath(destDir, file.RelativePath);
            try
            {
                progress?.Report(new(i, listing.Files.Count, file.RelativePath));
                await adb.PullAsync(ep, RemotePath.Absolute(rel, file.RelativePath), local,
                    new Relay(f => progress?.Report(new(i, listing.Files.Count, file.RelativePath, f))), ct);
                var length = new FileInfo(local).Length;
                if (length != file.Size)
                {
                    File.Delete(local);
                    errors.Add($"{file.RelativePath}: dimensione diversa dopo la copia ({file.Size} -> {length}).");
                    continue;
                }
                File.SetLastWriteTimeUtc(local, file.Modified.UtcDateTime);
                copied++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{file.RelativePath}: {ex.Message}");
            }
        }
        progress?.Report(new(listing.Files.Count, listing.Files.Count, ""));
        return new(listing.Files.Count, copied, errors);
    }

    /// <summary>Sends local files into a device folder. Never overwrites: a name already there is reported and skipped.</summary>
    public async Task<BulkResult> PushFilesAsync(AdbEndpoint ep, IReadOnlyList<string> localFiles, string folder,
        IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        if (!RemotePath.TryFolder(folder, out var rel))
            throw new ArgumentException($"Cartella non valida: {folder}", nameof(folder));

        var dir = RemotePath.Absolute(rel);
        var mk = (await adb.ShellAsync(ep, $"mkdir -p {RemotePath.Quote(dir)}", ct)).Trim();
        if (mk.Length > 0) throw new InvalidOperationException($"Cartella non creabile sul dispositivo: {mk}");

        var lister = new RemoteFileLister(adb);
        var existing = (await lister.ListFilesAsync(ep, rel, ct)).Files.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
        var errors = new List<string>();
        var sent = new Dictionary<string, long>(StringComparer.Ordinal);

        for (var i = 0; i < localFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var local = localFiles[i];
            var name = Path.GetFileName(local);
            progress?.Report(new(i, localFiles.Count, name));
            var partial = "";
            try
            {
                if (!File.Exists(local)) { errors.Add($"{name}: file non trovato."); continue; }
                if (!RemotePath.IsSafeRelativeFile(name) || name.Contains('/')) { errors.Add($"{name}: nome non valido per il dispositivo."); continue; }
                if (existing.Contains(name)) { errors.Add($"{name}: esiste già sul dispositivo, saltato."); continue; }

                var final = RemotePath.Absolute(rel, name);
                partial = $"{dir}/.aam-{Guid.NewGuid():N}.part";
                var info = new FileInfo(local);
                await adb.PushAsync(ep, local, partial, info.LastWriteTimeUtc,
                    new Relay(f => progress?.Report(new(i, localFiles.Count, name, f))), ct);
                var mv = (await adb.ShellAsync(ep, $"mv {RemotePath.Quote(partial)} {RemotePath.Quote(final)}", ct)).Trim();
                if (mv.Length > 0) { errors.Add($"{name}: rinomina fallita: {mv}"); continue; }
                partial = "";
                sent[name] = info.Length;
                existing.Add(name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{name}: {ex.Message}");
            }
            finally
            {
                if (partial.Length > 0)
                    try { await adb.ShellAsync(ep, $"rm -f {RemotePath.Quote(partial)}", CancellationToken.None); } catch { /* hidden leftover */ }
            }
        }

        // Count a file as copied only if the device now holds it at the same size.
        var copied = 0;
        if (sent.Count > 0)
        {
            var now = (await lister.ListFilesAsync(ep, rel, ct)).Files.ToDictionary(f => f.RelativePath, f => f.Size, StringComparer.Ordinal);
            foreach (var (name, size) in sent)
            {
                if (now.TryGetValue(name, out var onDevice) && onDevice == size) copied++;
                else errors.Add($"{name}: dimensione diversa sul dispositivo dopo l'invio.");
            }
        }
        progress?.Report(new(localFiles.Count, localFiles.Count, ""));
        return new(localFiles.Count, copied, errors);
    }

    public async Task ScreenshotAsync(AdbEndpoint ep, string localPath, CancellationToken ct = default)
    {
        var remote = $"{RemotePath.Root}/aam-screen-{Guid.NewGuid():N}.png";
        try
        {
            var output = (await adb.ShellAsync(ep, $"screencap -p {RemotePath.Quote(remote)}", ct)).Trim();
            if (output.Length > 0) throw new InvalidOperationException($"Screenshot non riuscito: {output}");
            await adb.PullAsync(ep, remote, localPath, null, ct);
        }
        finally
        {
            try { await adb.ShellAsync(ep, $"rm -f {RemotePath.Quote(remote)}", CancellationToken.None); } catch { /* best effort */ }
        }
    }

    public async Task RebootAsync(AdbEndpoint ep, CancellationToken ct = default) =>
        await adb.ShellAsync(ep, "reboot", ct);

    /// <summary>Android allows characters Windows rejects (: ? " * ...): swap them, and never overwrite a name that collides afterwards.</summary>
    private static string UniqueLocalPath(string destDir, string relative)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = relative.Split('/').Select(p =>
        {
            var clean = new string([.. p.Select(c => invalid.Contains(c) ? '_' : c)]).TrimEnd('.', ' ');
            return clean.Length == 0 ? "_" : clean;
        }).ToArray();
        var path = Path.Combine([destDir, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) return path;
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
            if (!File.Exists($"{stem}_{n}{ext}")) return $"{stem}_{n}{ext}";
    }
}
