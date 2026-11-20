using AndroidAppManager.Core.Packages;

namespace AndroidAppManager.Core.Tests;

public class BackupMigrationTests : IDisposable
{
    private readonly string _appData = Directory.CreateTempSubdirectory("appdata").FullName;
    public void Dispose() => Directory.Delete(_appData, true);

    private string Legacy => Path.Combine(_appData, "TvLauncher", "backups");
    private string Current => Path.Combine(_appData, "AndroidAppManager", "backups");

    [Fact]
    public void Moves_legacy_backups_once()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "a.json"), "{}");

        Assert.Equal(Current, BackupStore.ResolveDefaultDirectory(_appData));
        Assert.True(File.Exists(Path.Combine(Current, "a.json")));
        Assert.False(Directory.Exists(Legacy));

        // second call: nothing left to move, same answer
        Assert.Equal(Current, BackupStore.ResolveDefaultDirectory(_appData));
    }

    [Fact]
    public void Never_overwrites_an_existing_new_directory()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "old.json"), "{}");
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Current, "new.json"), "{}");

        Assert.Equal(Current, BackupStore.ResolveDefaultDirectory(_appData));
        Assert.True(File.Exists(Path.Combine(Current, "new.json")));
        Assert.True(File.Exists(Path.Combine(Legacy, "old.json"))); // left alone
    }

    [Fact]
    public void Fresh_install_returns_new_path_without_creating_it()
    {
        Assert.Equal(Current, BackupStore.ResolveDefaultDirectory(_appData));
        Assert.False(Directory.Exists(Current));
    }
}
