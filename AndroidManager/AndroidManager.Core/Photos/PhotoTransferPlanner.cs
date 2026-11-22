using AndroidManager.Core.Adb;
using AndroidManager.Core.Files;

namespace AndroidManager.Core.Photos;

/// <summary>Compares the chosen folders on the source with the same folders on the backup.</summary>
public sealed class PhotoTransferPlanner(IAdbService adb)
{
    /// <summary>Two files are "the same" when size matches and the dates are within this window.</summary>
    internal static readonly TimeSpan DateTolerance = TimeSpan.FromSeconds(2);

    public async Task<TransferPlan> BuildAsync(AdbEndpoint source, AdbEndpoint backup, IEnumerable<string> folders,
        CancellationToken ct = default)
    {
        if (source.Serial == backup.Serial)
            throw new ArgumentException("Sorgente e backup sono lo stesso dispositivo.");

        var valid = new List<string>();
        foreach (var f in folders)
        {
            if (!RemotePath.TryFolder(f, out var rel)) throw new ArgumentException($"Cartella non valida: {f}", nameof(folders));
            valid.Add(rel);
        }

        var lister = new RemoteFileLister(adb);
        var items = new List<PlanItem>();
        var unreadable = 0;
        foreach (var folder in RemotePath.DropNested(valid))
        {
            var src = await lister.ListFilesAsync(source, folder, ct);
            unreadable += src.Unreadable;
            var dst = await lister.ListFilesAsync(backup, folder, ct);
            // A file we cannot see on the backup might already exist there: sending over it could destroy it.
            if (dst.Unreadable > 0)
                throw new InvalidOperationException(
                    $"Il backup non permette di leggere {dst.Unreadable} elementi in {folder}: trasferimento rifiutato.");

            var onBackup = dst.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
            foreach (var file in src.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
            {
                var status = !onBackup.TryGetValue(file.RelativePath, out var other) ? ItemStatus.New
                    : other.Size == file.Size && (other.Modified - file.Modified).Duration() <= DateTolerance
                        ? ItemStatus.AlreadyPresent
                        : ItemStatus.Conflict;
                items.Add(new(folder, file, status));
            }
        }
        return new(items, unreadable);
    }
}
