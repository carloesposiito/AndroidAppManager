using AndroidManager.Core.Photos;
using AndroidManager.Core.Tests.Support;
using static AndroidManager.Core.Tests.Support.FakePhones;

namespace AndroidManager.Core.Tests;

public sealed class PhotoTransferTests : IDisposable
{
    private const string Cam = "/sdcard/DCIM/Camera/";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "aam-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakePhones _adb = new();
    private readonly Phone _src, _dst;

    public PhotoTransferTests()
    {
        _src = _adb.Add("SRC");
        _dst = _adb.Add("DST");
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { /* may not exist */ }
    }

    private Task<TransferPlan> PlanAsync(params string[] folders) =>
        new PhotoTransferPlanner(_adb).BuildAsync(Ep("SRC"), Ep("DST"), folders.Length == 0 ? ["DCIM/Camera"] : folders);

    private Task<TransferResult> RunAsync(TransferPlan plan, bool delete, CancellationToken ct = default) =>
        new PhotoTransferService(_adb, _temp).ExecuteAsync(Ep("SRC"), Ep("DST"), plan, delete, null, ct);

    private void ThreePhotos()
    {
        Put(_src, Cam + "a.jpg", "AAAA", 1_700_000_001);
        Put(_src, Cam + "b.jpg", "BBBBBB", 1_700_000_002);
        Put(_src, Cam + "2024/c.mp4", "CC", 1_700_000_003);
    }

    // ---------- planner ----------

    [Fact]
    public async Task Plan_classifies_new_present_and_conflict()
    {
        Put(_src, Cam + "new.jpg", "N");
        Put(_src, Cam + "same.jpg", "SAME", 1_700_000_050);
        Put(_dst, Cam + "same.jpg", "SAME", 1_700_000_051);       // within tolerance
        Put(_src, Cam + "diffsize.jpg", "LONGER");
        Put(_dst, Cam + "diffsize.jpg", "x");
        Put(_src, Cam + "diffdate.jpg", "SAME", 1_700_000_000);
        Put(_dst, Cam + "diffdate.jpg", "SAME", 1_700_009_000);

        var plan = await PlanAsync();
        Assert.Equal(["new.jpg"], plan.New.Select(i => i.File.RelativePath));
        Assert.Equal(["same.jpg"], plan.AlreadyPresent.Select(i => i.File.RelativePath));
        Assert.Equal(["diffdate.jpg", "diffsize.jpg"], plan.Conflicts.Select(i => i.File.RelativePath).Order());
        Assert.Equal(1, plan.NewBytes);
    }

    [Fact]
    public async Task Plan_ignores_hidden_files_and_selects_nested_folder_once()
    {
        Put(_src, Cam + "a.jpg", "A");
        Put(_src, Cam + ".trashed-1-a.jpg", "T");
        var plan = await PlanAsync("DCIM", "DCIM/Camera");
        Assert.Single(plan.Items);
        Assert.Equal("DCIM", plan.Items[0].Folder);
        Assert.Equal("Camera/a.jpg", plan.Items[0].File.RelativePath);
    }

    [Fact]
    public async Task Plan_refuses_same_device_and_unsafe_folder()
    {
        var planner = new PhotoTransferPlanner(_adb);
        await Assert.ThrowsAsync<ArgumentException>(() => planner.BuildAsync(Ep("SRC"), Ep("SRC"), ["DCIM"]));
        await Assert.ThrowsAsync<ArgumentException>(() => planner.BuildAsync(Ep("SRC"), Ep("DST"), ["../x"]));
    }

    [Fact]
    public async Task Plan_refuses_when_backup_listing_is_unreadable()
    {
        Put(_src, Cam + "a.jpg", "A");
        _dst.Unreadable.Add("/sdcard/DCIM");
        await Assert.ThrowsAsync<InvalidOperationException>(() => PlanAsync());
    }

    // ---------- service: happy path ----------

    [Fact]
    public async Task Moves_everything_and_deletes_source_after_verification()
    {
        ThreePhotos();
        var plan = await PlanAsync();
        var r = await RunAsync(plan, delete: true);

        Assert.False(r.Cancelled);
        Assert.Equal((3, 3, 3, 0), (r.Copied, r.Verified, r.Deleted, r.Failed));
        Assert.Empty(_src.Files);
        Assert.Equal(3, _dst.Files.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_003), _dst.Files[Cam + "2024/c.mp4"].Mtime); // date kept
        Assert.Equal("BBBBBB", System.Text.Encoding.UTF8.GetString(_dst.Files[Cam + "b.jpg"].Data));
        Assert.Contains(_dst.Dirs, d => d == "/sdcard/DCIM/Camera/2024");
        Assert.DoesNotContain(_dst.Files.Keys, k => k.Contains(".aam-"));
        Assert.False(Directory.EnumerateDirectories(Path.Combine(_temp, "AndroidManager"), "transfer-*").Any());
    }

    [Fact]
    public async Task Keeps_source_when_delete_is_off()
    {
        ThreePhotos();
        var r = await RunAsync(await PlanAsync(), delete: false);
        Assert.Equal((3, 3, 0), (r.Copied, r.Verified, r.Deleted));
        Assert.Equal(3, _src.Files.Count);
        Assert.DoesNotContain(_adb.Commands, c => c.StartsWith("rm "));
    }

    // ---------- service: nothing may be lost ----------

    [Fact]
    public async Task Size_mismatch_on_backup_keeps_that_file_on_source()
    {
        ThreePhotos();
        _adb.TruncatePushOn = 2;
        var r = await RunAsync(await PlanAsync(), delete: true);

        Assert.Equal(3, r.Copied);
        Assert.Equal(2, r.Verified);
        Assert.Equal(2, r.Deleted);
        Assert.Equal(1, r.Failed);
        var kept = Assert.Single(_src.Files);
        var failed = Assert.Single(r.Files, f => f.Error is not null);
        Assert.Equal(kept.Key, "/sdcard/" + failed.Item.Path);
        Assert.False(failed.Deleted);
    }

    [Fact]
    public async Task Pull_failure_leaves_source_and_backup_untouched_for_that_file()
    {
        ThreePhotos();
        _adb.FailPullOn = 1;
        var r = await RunAsync(await PlanAsync(), delete: true);

        Assert.Equal(2, r.Copied);
        Assert.Equal(1, r.Failed);
        Assert.Single(_src.Files);
        Assert.Equal(2, _dst.Files.Count);
    }

    [Fact]
    public async Task Push_failure_keeps_source_and_leaves_no_partial_file()
    {
        ThreePhotos();
        _adb.FailPushOn = 1;
        var r = await RunAsync(await PlanAsync(), delete: true);

        Assert.Equal(1, r.Failed);
        Assert.Single(_src.Files);
        Assert.DoesNotContain(_dst.Files.Keys, k => k.Contains(".aam-"));
    }

    [Fact]
    public async Task File_that_grew_after_planning_is_not_copied_or_deleted()
    {
        Put(_src, Cam + "rec.mp4", "12345");
        var plan = await PlanAsync();
        Put(_src, Cam + "rec.mp4", "1234567890"); // still recording

        var r = await RunAsync(plan, delete: true);
        Assert.Equal(0, r.Copied);
        Assert.Equal(1, r.Failed);
        Assert.Single(_src.Files);
        Assert.Empty(_dst.Files);
    }

    [Fact]
    public async Task Conflicts_are_left_alone_on_both_phones_even_with_delete_on()
    {
        Put(_src, Cam + "x.jpg", "SOURCE-VERSION");
        Put(_dst, Cam + "x.jpg", "other");
        var r = await RunAsync(await PlanAsync(), delete: true);

        Assert.Equal(1, r.Conflicts);
        Assert.Equal((0, 0, 0), (r.Copied, r.Verified, r.Deleted));
        Assert.Equal("SOURCE-VERSION", System.Text.Encoding.UTF8.GetString(_src.Files[Cam + "x.jpg"].Data));
        Assert.Equal("other", System.Text.Encoding.UTF8.GetString(_dst.Files[Cam + "x.jpg"].Data));
    }

    [Fact]
    public async Task Already_present_files_are_deleted_only_when_asked()
    {
        Put(_src, Cam + "s.jpg", "SAME", 1_700_000_050);
        Put(_dst, Cam + "s.jpg", "SAME", 1_700_000_050);

        var off = await RunAsync(await PlanAsync(), delete: false);
        Assert.Equal((0, 1, 0), (off.Copied, off.Verified, off.Deleted));
        Assert.Single(_src.Files);

        var on = await RunAsync(await PlanAsync(), delete: true);
        Assert.Equal((0, 1, 1), (on.Copied, on.Verified, on.Deleted));
        Assert.Empty(_src.Files);
        Assert.Single(_dst.Files);
    }

    [Fact]
    public async Task Delete_failure_is_reported_and_does_not_stop_the_others()
    {
        ThreePhotos();
        _src.Locked.Add(Cam + "a.jpg");
        var r = await RunAsync(await PlanAsync(), delete: true);

        Assert.Equal((3, 3, 2), (r.Copied, r.Verified, r.Deleted));
        var bad = Assert.Single(r.Files, f => f.Error is not null);
        Assert.False(bad.Deleted);
        Assert.Contains("non cancellato", bad.Error);
        Assert.Contains(Cam + "a.jpg", _src.Files.Keys);
    }

    [Fact]
    public async Task Cancel_mid_copy_deletes_nothing_and_cleans_temp()
    {
        ThreePhotos();
        using var cts = new CancellationTokenSource();
        _adb.AfterPull = n => { if (n == 2) cts.Cancel(); };

        var r = await RunAsync(await PlanAsync(), delete: true, cts.Token);

        Assert.True(r.Cancelled);
        Assert.Equal(0, r.Deleted);
        Assert.Equal(3, _src.Files.Count);
        Assert.DoesNotContain(_dst.Files.Keys, k => k.Contains(".aam-"));
        Assert.False(Directory.EnumerateDirectories(Path.Combine(_temp, "AndroidManager"), "transfer-*").Any());
    }

    // ---------- gallery refresh (so the photos show up on the backup) ----------

    private IEnumerable<string> IndexCommands => _adb.Commands.Where(c => c.StartsWith("for f in "));

    [Fact]
    public async Task Gallery_scan_covers_only_files_copied_and_verified_this_run()
    {
        ThreePhotos();
        Put(_src, Cam + "old.jpg", "SAME", 1_700_000_050);
        Put(_dst, Cam + "old.jpg", "SAME", 1_700_000_050);
        await RunAsync(await PlanAsync(), delete: false);

        var cmd = Assert.Single(IndexCommands);
        Assert.Contains("'/sdcard/DCIM/Camera/a.jpg'", cmd);
        Assert.Contains("'/sdcard/DCIM/Camera/2024/c.mp4'", cmd);
        Assert.DoesNotContain("old.jpg", cmd);
        Assert.Contains("MEDIA_SCANNER_SCAN_FILE", cmd);
    }

    [Fact]
    public async Task Gallery_scan_runs_in_batches_of_20()
    {
        for (var i = 0; i < 45; i++) Put(_src, $"{Cam}p{i:D2}.jpg", "x");
        await RunAsync(await PlanAsync(), delete: false);
        Assert.Equal(3, IndexCommands.Count());
    }

    [Fact]
    public async Task Gallery_scan_skips_unverified_files_and_does_nothing_when_nothing_was_copied()
    {
        Put(_src, Cam + "a.jpg", "AAAA");
        _adb.TruncatePushOn = 1;
        await RunAsync(await PlanAsync(), delete: false);
        Assert.Empty(IndexCommands);
    }

    [Fact]
    public async Task Gallery_scan_failure_does_not_fail_the_transfer()
    {
        ThreePhotos();
        _adb.ThrowOnIndex = true;
        var r = await RunAsync(await PlanAsync(), delete: true);
        Assert.False(r.Cancelled);
        Assert.Equal((3, 3, 3, 0), (r.Copied, r.Verified, r.Deleted, r.Failed));
    }

    [Fact]
    public async Task Service_refuses_same_device()
    {
        var plan = new TransferPlan([]);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new PhotoTransferService(_adb, _temp).ExecuteAsync(Ep("SRC"), Ep("SRC"), plan, true));
    }

    [Fact]
    public async Task Reports_phases_in_order()
    {
        ThreePhotos();
        var plan = await PlanAsync();
        var phases = new List<TransferPhase>();
        var progress = new SyncProgress<TransferProgress>(p => { if (phases.Count == 0 || phases[^1] != p.Phase) phases.Add(p.Phase); });
        await new PhotoTransferService(_adb, _temp).ExecuteAsync(Ep("SRC"), Ep("DST"), plan, true, progress);
        Assert.Equal([TransferPhase.Downloading, TransferPhase.Uploading, TransferPhase.Downloading, TransferPhase.Uploading,
            TransferPhase.Downloading, TransferPhase.Uploading, TransferPhase.Verifying, TransferPhase.Deleting,
            TransferPhase.Indexing], phases);
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; tests need the callbacks in order, inline.</summary>
    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}
