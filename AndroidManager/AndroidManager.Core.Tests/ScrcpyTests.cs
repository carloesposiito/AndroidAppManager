using AndroidManager.Core.Mirror;

namespace AndroidManager.Core.Tests;

public sealed class ScrcpyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aam-scrcpy-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* may not exist */ }
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Find_prefers_explicit_then_bundled_then_path()
    {
        var explicitExe = Touch("mine", "scrcpy.exe");
        var bundled = Touch("app", "scrcpy", "scrcpy.exe");
        var onPath = Touch("bin", "scrcpy.exe");
        var appDir = Path.Combine(_dir, "app");
        var pathEnv = Path.Combine(_dir, "bin");

        Assert.Equal(explicitExe, ScrcpyLocator.Find(explicitExe, appDir, pathEnv));
        Assert.Equal(bundled, ScrcpyLocator.Find(null, appDir, pathEnv));
        Assert.Equal(bundled, ScrcpyLocator.Find(Path.Combine(_dir, "gone.exe"), appDir, pathEnv));
        Assert.Equal(onPath, ScrcpyLocator.Find(null, Path.Combine(_dir, "empty"), pathEnv));
    }

    [Fact]
    public void Find_returns_null_when_absent_and_survives_bad_path_entries()
    {
        Assert.Null(ScrcpyLocator.Find(null, _dir, ""));
        Assert.Null(ScrcpyLocator.Find(null, _dir, "<>|:" + Path.PathSeparator + "\"C:\\nope\""));
    }

    [Fact]
    public void Arguments_target_the_serial_and_a_borderless_titled_window()
    {
        var a = ScrcpyLauncher.Arguments("192.168.1.9:5555", "AAM-1");
        Assert.Equal(["--serial", "192.168.1.9:5555", "--window-title", "AAM-1", "--window-borderless", "--no-audio"], a);
    }

    [Fact]
    public void Arguments_add_the_panel_size_only_when_known()
    {
        var sized = ScrcpyLauncher.Arguments("s", "t", 640, 900);
        Assert.Equal(["--window-width", "640", "--window-height", "900"], sized.Skip(6));
        Assert.Equal(6, ScrcpyLauncher.Arguments("s", "t", 0, 900).Count);
        Assert.Equal(6, ScrcpyLauncher.Arguments("s", "t", 640, 0).Count);
    }

    [Fact]
    public void Arguments_keep_odd_serials_as_one_argument()
    {
        var a = ScrcpyLauncher.Arguments("a b;c", "t");
        Assert.Equal("a b;c", a[1]);
    }

    [Fact]
    public void Arguments_reject_blank_serial()
    {
        Assert.Throws<ArgumentException>(() => ScrcpyLauncher.Arguments(" ", "t"));
    }

    [Fact]
    public void WindowTitle_is_unique_per_call()
    {
        Assert.NotEqual(ScrcpyLauncher.NewWindowTitle(), ScrcpyLauncher.NewWindowTitle());
        Assert.StartsWith("AndroidManager-", ScrcpyLauncher.NewWindowTitle());
    }
}
