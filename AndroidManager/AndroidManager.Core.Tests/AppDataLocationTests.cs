using AndroidManager.Core;

namespace AndroidManager.Core.Tests;

public sealed class AppDataLocationTests : IDisposable
{
    private readonly string _appData = Directory.CreateTempSubdirectory("appdata").FullName;
    public void Dispose() => Directory.Delete(_appData, true);

    private string Old => Path.Combine(_appData, "AndroidAppManager");
    private string New => Path.Combine(_appData, "AndroidManager");

    [Fact]
    public void Fresh_install_uses_the_new_name_without_creating_it()
    {
        Assert.Equal(New, AppDataLocation.Resolve(_appData));
        Assert.False(Directory.Exists(New));
    }

    [Fact]
    public void Old_folder_moves_over_with_settings_and_backups()
    {
        Directory.CreateDirectory(Path.Combine(Old, "backups"));
        File.WriteAllText(Path.Combine(Old, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(Old, "backups", "a.json"), "{}");

        Assert.Equal(New, AppDataLocation.Resolve(_appData));
        Assert.True(File.Exists(Path.Combine(New, "settings.json")));
        Assert.True(File.Exists(Path.Combine(New, "backups", "a.json")));
        Assert.False(Directory.Exists(Old));
        Assert.Equal(New, AppDataLocation.Resolve(_appData)); // second call: same answer
    }

    [Fact]
    public void Never_overwrites_an_existing_new_folder()
    {
        Directory.CreateDirectory(Old);
        File.WriteAllText(Path.Combine(Old, "old.txt"), "x");
        Directory.CreateDirectory(New);
        File.WriteAllText(Path.Combine(New, "new.txt"), "y");

        Assert.Equal(New, AppDataLocation.Resolve(_appData));
        Assert.True(File.Exists(Path.Combine(New, "new.txt")));
        Assert.True(File.Exists(Path.Combine(Old, "old.txt")));
    }
}
