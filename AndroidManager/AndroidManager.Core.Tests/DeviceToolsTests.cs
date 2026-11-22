using AndroidManager.Core.Files;
using AndroidManager.Core.Tests.Support;
using static AndroidManager.Core.Tests.Support.FakePhones;

namespace AndroidManager.Core.Tests;

public sealed class DeviceToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aam-tools-" + Guid.NewGuid().ToString("N"));
    private readonly FakePhones _adb = new();
    private readonly Phone _phone;

    public DeviceToolsTests() => _phone = _adb.Add("P1");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* may not exist */ }
    }

    [Fact]
    public async Task CopyFolderToPc_keeps_structure_and_reports_counts()
    {
        Put(_phone, "/sdcard/Pictures/a.jpg", "AAA");
        Put(_phone, "/sdcard/Pictures/trip/b.jpg", "BB");
        var dest = Path.Combine(_dir, "out");

        var r = await new DeviceTools(_adb).CopyFolderToPcAsync(Ep("P1"), "Pictures", dest);

        Assert.Equal((2, 2, 0), (r.Total, r.Copied, r.Failed));
        Assert.Equal("AAA", await File.ReadAllTextAsync(Path.Combine(dest, "a.jpg")));
        Assert.Equal("BB", await File.ReadAllTextAsync(Path.Combine(dest, "trip", "b.jpg")));
    }

    [Fact]
    public async Task CopyFolderToPc_refuses_non_empty_destination()
    {
        Put(_phone, "/sdcard/Pictures/a.jpg", "A");
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "keep.txt"), "x");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeviceTools(_adb).CopyFolderToPcAsync(Ep("P1"), "Pictures", _dir));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task CopyFolderToPc_sanitizes_names_windows_rejects_and_counts_failures()
    {
        Put(_phone, "/sdcard/D/ok.txt", "1");
        Put(_phone, "/sdcard/D/a:b?.txt", "2");
        Put(_phone, "/sdcard/D/bad.txt", "3");
        _adb.FailPullOn = 3;
        var dest = Path.Combine(_dir, "o");

        var r = await new DeviceTools(_adb).CopyFolderToPcAsync(Ep("P1"), "D", dest);

        Assert.Equal((3, 2, 1), (r.Total, r.Copied, r.Failed));
        Assert.Equal(2, Directory.GetFiles(dest).Length);
        Assert.DoesNotContain(Directory.GetFiles(dest), f => f.EndsWith(".part"));
    }

    [Fact]
    public async Task PushFiles_sends_to_folder_and_skips_existing_names()
    {
        Directory.CreateDirectory(_dir);
        var a = Path.Combine(_dir, "a.pdf");
        var b = Path.Combine(_dir, "b.pdf");
        await File.WriteAllTextAsync(a, "AAA");
        await File.WriteAllTextAsync(b, "BBBB");
        Put(_phone, "/sdcard/Documents/b.pdf", "old");

        var r = await new DeviceTools(_adb).PushFilesAsync(Ep("P1"), [a, b, Path.Combine(_dir, "missing.pdf")], "Documents");

        Assert.Equal((3, 1, 2), (r.Total, r.Copied, r.Failed));
        Assert.Equal("AAA", System.Text.Encoding.UTF8.GetString(_phone.Files["/sdcard/Documents/a.pdf"].Data));
        Assert.Equal("old", System.Text.Encoding.UTF8.GetString(_phone.Files["/sdcard/Documents/b.pdf"].Data));
        Assert.DoesNotContain(_phone.Files.Keys, k => k.Contains(".aam-"));
    }

    [Fact]
    public async Task PushFiles_rejects_unsafe_target_folder()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new DeviceTools(_adb).PushFilesAsync(Ep("P1"), [], "../x"));
    }

    [Fact]
    public async Task Screenshot_saves_png_and_removes_the_temp_file_on_the_phone()
    {
        var local = Path.Combine(_dir, "shot.png");
        await new DeviceTools(_adb).ScreenshotAsync(Ep("P1"), local);
        Assert.Equal("PNG", await File.ReadAllTextAsync(local));
        Assert.Empty(_phone.Files);
    }

    [Fact]
    public async Task Reboot_sends_reboot()
    {
        await new DeviceTools(_adb).RebootAsync(Ep("P1"));
        Assert.Equal("reboot", _adb.Commands.Single());
    }
}
