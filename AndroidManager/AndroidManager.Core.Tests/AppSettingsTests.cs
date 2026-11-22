using AndroidManager.Core.Adb;
using AndroidManager.Core.Settings;

namespace AndroidManager.Core.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aam-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* may not exist */ }
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = AppSettings.Load(Path.Combine(_dir, "none.json"));
        Assert.False(s.HasBackup);
        Assert.Equal(AdbEndpoint.DefaultPort, s.LastPort);
        Assert.Equal(["DCIM/Camera"], s.PhotoFolders);
    }

    [Fact]
    public void Save_then_load_round_trips_and_creates_the_folder()
    {
        var path = Path.Combine(_dir, "sub", "settings.json");
        var saved = new AppSettings { BackupModel = "Pixel 5", BackupProduct = "redfin", BackupSerial = "ABC", LastHost = "10.0.0.5", ScrcpyPath = "D:/tools/scrcpy.exe", LastPort = 5556, PhotoFolders = ["Pictures"] };
        saved.Save(path);
        var loaded = AppSettings.Load(path);
        Assert.Equal("Pixel 5", loaded.BackupModel);
        Assert.Equal("redfin", loaded.BackupProduct);
        Assert.Equal("ABC", loaded.BackupSerial);
        Assert.Equal("10.0.0.5", loaded.LastHost);
        Assert.Equal(5556, loaded.LastPort);
        Assert.Equal("D:/tools/scrcpy.exe", loaded.ScrcpyPath);
        Assert.Equal(["Pictures"], loaded.PhotoFolders);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("null")]
    public void Corrupt_file_gives_defaults_instead_of_crashing(string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "s.json");
        File.WriteAllText(path, content);
        Assert.Equal(new AppSettings().BackupSerial, AppSettings.Load(path).BackupSerial);
    }

    [Fact]
    public void Folders_removed_when_invalid_on_load()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "s.json");
        File.WriteAllText(path, """{"PhotoFolders":["DCIM/Camera","../etc","", "Pictures"]}""");
        Assert.Equal(["DCIM/Camera", "Pictures"], AppSettings.Load(path).PhotoFolders);
        File.WriteAllText(path, """{"PhotoFolders":[]}""");
        Assert.Equal(["DCIM/Camera"], AppSettings.Load(path).PhotoFolders);
    }

    [Theory]
    [InlineData("ABC", "Other", "x", true)]         // serial wins
    [InlineData("zzz", "Pixel 5", "redfin", true)]  // wifi serial changed: model+product still match
    [InlineData("zzz", "Pixel 5", "other", false)]
    [InlineData("zzz", "Galaxy", "redfin", false)]
    public void IsBackup_matches_by_serial_or_model_and_product(string serial, string model, string product, bool expected)
    {
        var s = new AppSettings { BackupSerial = "ABC", BackupModel = "Pixel 5", BackupProduct = "redfin" };
        Assert.Equal(expected, s.IsBackup(new AdbDevice(serial, model, product, true, "Online")));
    }

    [Fact]
    public void IsBackup_is_false_when_none_configured()
    {
        Assert.False(new AppSettings().IsBackup(new AdbDevice("", "", "", true, "Online")));
        Assert.False(new AppSettings().IsBackup(new AdbDevice("A", "M", "P", true, "Online")));
    }
}
