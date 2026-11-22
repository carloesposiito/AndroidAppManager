using AndroidManager.Core.Files;

namespace AndroidManager.Core.Photos;

public enum ItemStatus
{
    /// <summary>Not on the backup: will be copied.</summary>
    New,
    /// <summary>Same name, size and modification time on the backup: nothing to copy.</summary>
    AlreadyPresent,
    /// <summary>Same name on the backup with a different size or date: left alone on both phones.</summary>
    Conflict
}

public sealed record PlanItem(string Folder, RemoteFile File, ItemStatus Status)
{
    /// <summary>Path below /sdcard.</summary>
    public string Path => $"{Folder}/{File.RelativePath}";
}

/// <param name="SourceUnreadable">Entries on the source the listing could not read: they are not part of the plan.</param>
public sealed record TransferPlan(IReadOnlyList<PlanItem> Items, int SourceUnreadable = 0)
{
    public IEnumerable<PlanItem> New => Items.Where(i => i.Status == ItemStatus.New);
    public IEnumerable<PlanItem> AlreadyPresent => Items.Where(i => i.Status == ItemStatus.AlreadyPresent);
    public IEnumerable<PlanItem> Conflicts => Items.Where(i => i.Status == ItemStatus.Conflict);
    public long NewBytes => New.Sum(i => i.File.Size);
}

public enum TransferPhase { Downloading, Uploading, Verifying, Deleting, Indexing }

/// <param name="Done">Files finished in this phase so far.</param>
/// <param name="FileFraction">0..1 progress of the file being moved (Downloading/Uploading only).</param>
public sealed record TransferProgress(TransferPhase Phase, int Done, int Total, string Current, double FileFraction = 0);

/// <param name="Copied">Sent to the backup in this run.</param>
/// <param name="Verified">Present on the backup with the source's size (copied now, or already there).</param>
/// <param name="Deleted">Removed from the source. Only ever true for a verified file.</param>
public sealed record FileResult(PlanItem Item, bool Copied, bool Verified, bool Deleted, string? Error);

public sealed record TransferResult(IReadOnlyList<FileResult> Files, bool Cancelled)
{
    public int Copied => Files.Count(f => f.Copied);
    public int Verified => Files.Count(f => f.Verified);
    public int Deleted => Files.Count(f => f.Deleted);
    public int Failed => Files.Count(f => f.Error is not null);
    public int Conflicts => Files.Count(f => f.Item.Status == ItemStatus.Conflict);
}
