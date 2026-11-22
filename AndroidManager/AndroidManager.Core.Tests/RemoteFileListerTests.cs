using AndroidManager.Core.Files;
using AndroidManager.Core.Tests.Support;

namespace AndroidManager.Core.Tests;

public class RemoteFileListerTests
{
    [Fact]
    public async Task ListFiles_parses_size_mtime_and_strips_folder_prefix()
    {
        var adb = new FakeAdb(_ =>
            "1000 1700000000 /sdcard/DCIM/Camera/IMG_1.jpg\n" +
            "2048 1700000100 /sdcard/DCIM/Camera/2024/Foto di Perù.jpg\n" +
            "5 1700000200 /sdcard/DCIM/Camera//x y.mp4\r\n");
        var r = await new RemoteFileLister(adb).ListFilesAsync(FakeAdb.Ep(), "DCIM/Camera");

        Assert.Equal(0, r.Unreadable);
        Assert.Equal(3, r.Files.Count);
        Assert.Equal(new RemoteFile("IMG_1.jpg", 1000, DateTimeOffset.FromUnixTimeSeconds(1700000000)), r.Files[0]);
        Assert.Equal("2024/Foto di Perù.jpg", r.Files[1].RelativePath);
        Assert.Equal("x y.mp4", r.Files[2].RelativePath);
        Assert.Contains("'/sdcard/DCIM/Camera/'", adb.Commands.Single());
    }

    [Fact]
    public async Task ListFiles_skips_hidden_and_counts_unreadable_lines()
    {
        var adb = new FakeAdb(_ =>
            "10 1700000000 /sdcard/P/ok.jpg\n" +
            "10 1700000000 /sdcard/P/.thumbnails/t.jpg\n" +
            "10 1700000000 /sdcard/P/.trashed-1-a.jpg\n" +
            "stat: '/sdcard/P/locked.jpg': Permission denied\n");
        var r = await new RemoteFileLister(adb).ListFilesAsync(FakeAdb.Ep(), "P");

        Assert.Equal(["ok.jpg"], r.Files.Select(f => f.RelativePath));
        Assert.Equal(1, r.Unreadable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("find: '/sdcard/Nope/': No such file or directory\n")]
    public async Task ListFiles_empty_or_missing_folder_is_empty_not_error(string output)
    {
        var r = await new RemoteFileLister(new FakeAdb(_ => output)).ListFilesAsync(FakeAdb.Ep(), "Nope");
        Assert.Empty(r.Files);
        Assert.Equal(0, r.Unreadable);
    }

    [Fact]
    public async Task ListFiles_rejects_unsafe_folder_without_running_anything()
    {
        var adb = new FakeAdb(_ => "");
        await Assert.ThrowsAsync<ArgumentException>(() => new RemoteFileLister(adb).ListFilesAsync(FakeAdb.Ep(), "../etc"));
        Assert.Empty(adb.Commands);
    }

    [Fact]
    public async Task ListFolders_returns_two_levels_without_hidden_or_android()
    {
        var adb = new FakeAdb(_ =>
            "/sdcard/DCIM\n/sdcard/DCIM/Camera\n/sdcard/DCIM/.thumbnails\n/sdcard/Android\n" +
            "/sdcard/Android/data\n/sdcard/Pictures\n/sdcard/Pictures/Screenshots\n/sdcard/.hidden\n");
        var folders = await new RemoteFileLister(adb).ListFoldersAsync(FakeAdb.Ep());
        Assert.Equal(["DCIM", "DCIM/Camera", "Pictures", "Pictures/Screenshots"], folders);
    }

    [Fact]
    public async Task Summarize_sums_files_per_folder()
    {
        var adb = new FakeAdb(c => c.Contains("'/sdcard/A/'")
            ? "100 1 /sdcard/A/a\n200 1 /sdcard/A/b\n"
            : "7 1 /sdcard/B/c\n");
        var s = await new RemoteFileLister(adb).SummarizeAsync(FakeAdb.Ep(), ["A", "B"]);
        Assert.Equal([new FolderSummary("A", 2, 300), new FolderSummary("B", 1, 7)], s);
    }
}
