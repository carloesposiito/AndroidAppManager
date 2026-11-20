using AndroidAppManager.Core.Adb;

namespace AndroidAppManager.Core.Tests;

public class AdbLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("adbloc").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    private string MakeAdb(params string[] parts)
    {
        var dir = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "adb.exe");
        File.WriteAllText(file, "");
        return file;
    }

    [Fact]
    public void Explicit_path_wins()
    {
        var explicitAdb = MakeAdb("explicit");
        MakeAdb("app", "adb");
        var found = AdbLocator.Find(explicitAdb, Path.Combine(_root, "app"), "");
        Assert.Equal(explicitAdb, found);
    }

    [Fact]
    public void Bundled_folder_before_PATH()
    {
        var bundled = MakeAdb("app", "adb");
        var onPath = MakeAdb("onpath");
        var found = AdbLocator.Find(null, Path.Combine(_root, "app"), Path.GetDirectoryName(onPath)!);
        Assert.Equal(bundled, found);
    }

    [Fact]
    public void Falls_back_to_PATH()
    {
        var onPath = MakeAdb("onpath");
        var found = AdbLocator.Find(null, Path.Combine(_root, "empty"), Path.GetDirectoryName(onPath)!);
        Assert.Equal(onPath, found);
    }

    [Fact]
    public void Missing_explicit_path_is_skipped()
    {
        var bundled = MakeAdb("app", "adb");
        var found = AdbLocator.Find(Path.Combine(_root, "nope", "adb.exe"), Path.Combine(_root, "app"), "");
        Assert.Equal(bundled, found);
    }

    [Fact]
    public void Returns_null_when_absent()
    {
        Assert.Null(AdbLocator.Find(null, Path.Combine(_root, "x"), ""));
    }
}
