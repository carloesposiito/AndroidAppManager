using AndroidManager.Core.Adb;
using AndroidManager.Core.Files;

namespace AndroidManager.Core.Photos;

/// <summary>
/// Moves the files of a <see cref="TransferPlan"/> from the source phone to the backup phone through the PC,
/// one file at a time (so the PC needs room for one file, not the whole library).
/// The source is touched last, and only for files the backup is proven to hold:
/// copy -> re-list the backup -> size must match -> only then delete from the source.
/// </summary>
public sealed class PhotoTransferService(IAdbService adb, string? tempRoot = null)
{
    private sealed class Work(PlanItem item)
    {
        public PlanItem Item { get; } = item;
        public bool Copied, Verified, Deleted;
        public string? Error;
        public FileResult ToResult() => new(Item, Copied, Verified, Deleted, Error);
    }

    /// <summary>Forwards in call order on the calling thread; Progress&lt;T&gt; would reorder through the thread pool.</summary>
    private sealed class Relay(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }

    public async Task<TransferResult> ExecuteAsync(AdbEndpoint source, AdbEndpoint backup, TransferPlan plan,
        bool deleteFromSource, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        if (source.Serial == backup.Serial)
            throw new ArgumentException("Sorgente e backup sono lo stesso dispositivo.");

        var work = plan.Items.Select(i => new Work(i)).ToList();
        foreach (var w in work.Where(w => w.Item.Status == ItemStatus.AlreadyPresent)) w.Verified = true;

        var temp = Path.Combine(tempRoot ?? Path.GetTempPath(), "AndroidManager", "transfer-" + Guid.NewGuid().ToString("N")[..8]);
        var cancelled = false;
        try
        {
            Directory.CreateDirectory(temp);
            cancelled = !await CopyAsync(source, backup, work, temp, progress, ct);
            if (!cancelled) await VerifyAsync(backup, work, progress, ct);
            if (!cancelled && deleteFromSource) cancelled = !await DeleteAsync(source, work, progress, ct);
            if (!cancelled) await IndexAsync(backup, work, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* temp is disposable */ }
        }
        return new([.. work.Select(w => w.ToResult())], cancelled);
    }

    /// <returns>false when cancelled.</returns>
    private async Task<bool> CopyAsync(AdbEndpoint source, AdbEndpoint backup, List<Work> work, string temp,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var todo = work.Where(w => w.Item.Status == ItemStatus.New).ToList();
        var madeDirs = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < todo.Count; i++)
        {
            if (ct.IsCancellationRequested) return false;
            var w = todo[i];
            var item = w.Item;
            var local = Path.Combine(temp, $"{i}.tmp");
            var partial = "";
            try
            {
                Report(TransferPhase.Downloading, 0);
                await adb.PullAsync(source, RemotePath.Absolute(item.Folder, item.File.RelativePath), local,
                    new Relay(f => Report(TransferPhase.Downloading, f)), ct);

                // A video still being recorded grows during the pull: a short copy must never count as a backup.
                var length = new FileInfo(local).Length;
                if (length != item.File.Size)
                {
                    w.Error = $"Il file è cambiato durante la copia ({item.File.Size} -> {length} byte).";
                    continue;
                }

                var final = RemotePath.Absolute(item.Folder, item.File.RelativePath);
                var dir = final[..final.LastIndexOf('/')];
                if (madeDirs.Add(dir))
                {
                    var mk = await adb.ShellAsync(backup, $"mkdir -p {RemotePath.Quote(dir)}", ct);
                    if (mk.Trim().Length > 0) { madeDirs.Remove(dir); w.Error = $"Cartella non creabile sul backup: {mk.Trim()}"; continue; }
                }

                // Hidden temp name: the file only gets its real name once it is complete.
                partial = $"{dir}/.aam-{Guid.NewGuid():N}.part";
                Report(TransferPhase.Uploading, 0);
                await adb.PushAsync(backup, local, partial, item.File.Modified,
                    new Relay(f => Report(TransferPhase.Uploading, f)), ct);
                var mv = await adb.ShellAsync(backup, $"mv {RemotePath.Quote(partial)} {RemotePath.Quote(final)}", ct);
                if (mv.Trim().Length > 0) { w.Error = $"Rinomina sul backup fallita: {mv.Trim()}"; continue; }
                partial = "";
                w.Copied = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { await DropPartialAsync(backup, partial); return false; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                w.Error = ex.Message;
            }
            finally
            {
                try { File.Delete(local); } catch { /* temp */ }
                if (partial.Length > 0 && w.Error is not null) await DropPartialAsync(backup, partial);
            }

            void Report(TransferPhase phase, double fraction) =>
                progress?.Report(new(phase, i, todo.Count, item.Path, fraction));
        }
        return true;
    }

    private async Task DropPartialAsync(AdbEndpoint backup, string partial)
    {
        if (partial.Length == 0) return;
        try { await adb.ShellAsync(backup, $"rm -f {RemotePath.Quote(partial)}", CancellationToken.None); }
        catch { /* a hidden .part file is harmless */ }
    }

    /// <summary>Re-reads the backup (not our own bookkeeping) and checks each copied file's size.</summary>
    private async Task VerifyAsync(AdbEndpoint backup, List<Work> work, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var copied = work.Where(w => w.Copied).ToList();
        var lister = new RemoteFileLister(adb);
        var done = 0;
        foreach (var group in copied.GroupBy(w => w.Item.Folder))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(TransferPhase.Verifying, done, copied.Count, group.Key));
            var listing = await lister.ListFilesAsync(backup, group.Key, ct);
            var onBackup = listing.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
            foreach (var w in group)
            {
                if (onBackup.TryGetValue(w.Item.File.RelativePath, out var f) && f.Size == w.Item.File.Size)
                    w.Verified = true;
                else
                    w.Error = onBackup.ContainsKey(w.Item.File.RelativePath)
                        ? "Dimensione diversa sul backup: il file resta sul sorgente."
                        : "File non trovato sul backup dopo la copia: resta sul sorgente.";
                done++;
            }
        }
        progress?.Report(new(TransferPhase.Verifying, copied.Count, copied.Count, ""));
    }

    /// <summary>
    /// A file pushed over adb does not show in Gallery / Google Photos until the media scanner sees it.
    /// Cosmetic and best effort: a failure or a cancel here never touches the transfer result.
    /// </summary>
    private async Task IndexAsync(AdbEndpoint backup, List<Work> work, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        const int batch = 20;
        var paths = work.Where(w => w.Copied && w.Verified)
            .Select(w => RemotePath.Absolute(w.Item.Folder, w.Item.File.RelativePath)).ToList();
        try
        {
            for (var i = 0; i < paths.Count; i += batch)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(TransferPhase.Indexing, i, paths.Count, "Aggiorno la galleria"));
                var list = string.Join(' ', paths.Skip(i).Take(batch).Select(RemotePath.Quote));
                await adb.ShellAsync(backup,
                    $"for f in {list}; do am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d \"file://$f\" >/dev/null 2>&1; done", ct);
            }
            if (paths.Count > 0) progress?.Report(new(TransferPhase.Indexing, paths.Count, paths.Count, ""));
        }
        catch (Exception) { /* best effort: photos are on the backup either way */ }
    }

    /// <returns>false when cancelled.</returns>
    private async Task<bool> DeleteAsync(AdbEndpoint source, List<Work> work, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var verified = work.Where(w => w.Verified && w.Error is null).ToList();
        for (var i = 0; i < verified.Count; i++)
        {
            if (ct.IsCancellationRequested) return false;
            var w = verified[i];
            progress?.Report(new(TransferPhase.Deleting, i, verified.Count, w.Item.Path));
            var path = RemotePath.Quote(RemotePath.Absolute(w.Item.Folder, w.Item.File.RelativePath));
            try
            {
                var output = (await adb.ShellAsync(source, $"rm -f {path}; [ -e {path} ] && echo STILL_THERE", ct)).Trim();
                if (output.Length == 0) w.Deleted = true;
                else w.Error = $"Copiato e verificato, ma non cancellato dal sorgente: {output}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                w.Error = $"Copiato e verificato, ma non cancellato dal sorgente: {ex.Message}";
            }
        }
        progress?.Report(new(TransferPhase.Deleting, verified.Count, verified.Count, ""));
        return true;
    }
}
