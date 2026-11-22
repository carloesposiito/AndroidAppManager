using System.IO.Compression;
using System.Text;
using AndroidManager.Core.Mirror;

namespace AndroidManager.Core.Tests;

public sealed class ScrcpyBundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aam-bundle-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* may not exist */ }
    }

    private static MemoryStream Zip(params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                var e = z.CreateEntry(name);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Extract_strips_the_single_top_folder_and_returns_the_exe()
    {
        using var zip = Zip(("scrcpy-win64-v3.3.4/", ""), ("scrcpy-win64-v3.3.4/scrcpy.exe", "EXE"),
            ("scrcpy-win64-v3.3.4/sub/scrcpy-server", "SRV"));
        var exe = ScrcpyBundle.Extract(zip, _dir);

        Assert.Equal(Path.Combine(_dir, "scrcpy.exe"), exe);
        Assert.Equal("EXE", File.ReadAllText(exe));
        Assert.Equal("SRV", File.ReadAllText(Path.Combine(_dir, "sub", "scrcpy-server")));
    }

    [Fact]
    public void Extract_keeps_paths_when_there_is_no_single_root_and_overwrites_on_rerun()
    {
        using (var zip = Zip(("scrcpy.exe", "OLD"), ("a.dll", "A")))
            ScrcpyBundle.Extract(zip, _dir);
        using (var zip = Zip(("scrcpy.exe", "NEW"), ("a.dll", "A")))
            ScrcpyBundle.Extract(zip, _dir);
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(_dir, "scrcpy.exe")));
    }

    [Fact]
    public void Extract_refuses_entries_that_escape_the_folder()
    {
        using var zip = Zip(("x/scrcpy.exe", "E"), ("x/../../evil.txt", "bad"));
        Assert.Throws<InvalidDataException>(() => ScrcpyBundle.Extract(zip, _dir));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "evil.txt")));
    }

    [Fact]
    public void Extract_fails_when_the_archive_has_no_scrcpy_exe()
    {
        using var zip = Zip(("root/readme.txt", "hi"));
        Assert.Throws<InvalidDataException>(() => ScrcpyBundle.Extract(zip, _dir));
    }

    [Fact]
    public void Locator_finds_the_extracted_copy_after_the_bundled_folder()
    {
        Directory.CreateDirectory(_dir);
        var extracted = Path.Combine(_dir, "ex", "scrcpy.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(extracted)!);
        File.WriteAllText(extracted, "");
        Assert.Equal(extracted, ScrcpyLocator.Find(null, Path.Combine(_dir, "none"), "", Path.Combine(_dir, "ex")));
        Assert.Null(ScrcpyLocator.Find(null, Path.Combine(_dir, "none"), "", Path.Combine(_dir, "nowhere")));
    }
}
