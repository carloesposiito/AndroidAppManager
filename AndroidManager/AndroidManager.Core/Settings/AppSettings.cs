using System.Text.Json;
using AndroidManager.Core.Adb;
using AndroidManager.Core.Files;

namespace AndroidManager.Core.Settings;

/// <summary>What the app remembers between runs: which phone is the backup, last Wi-Fi address, chosen photo folders.</summary>
public sealed record AppSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath { get; } =
        Path.Combine(AppDataLocation.Root, "settings.json");

    public string BackupSerial { get; init; } = "";
    public string BackupModel { get; init; } = "";
    public string BackupProduct { get; init; } = "";
    public string LastHost { get; init; } = "";
    /// <summary>scrcpy.exe chosen by the user; empty = look in the app folder and PATH.</summary>
    public string ScrcpyPath { get; init; } = "";
    public int LastPort { get; init; } = AdbEndpoint.DefaultPort;
    public IReadOnlyList<string> PhotoFolders { get; init; } = ["DCIM/Camera"];

    public bool HasBackup => BackupSerial.Length > 0 || BackupModel.Length > 0;

    /// <summary>
    /// The serial identifies a USB phone for good, but a Wi-Fi serial (ip:port) changes, so model + product
    /// is the fallback. Model alone is not enough: two identical phones share it.
    /// </summary>
    public bool IsBackup(AdbDevice d)
    {
        if (BackupSerial.Length > 0 && d.Serial == BackupSerial) return true;
        return BackupModel.Length > 0 && BackupProduct.Length > 0
            && string.Equals(d.Model, BackupModel, StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.Product, BackupProduct, StringComparison.OrdinalIgnoreCase);
    }

    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new();
            var folders = (s.PhotoFolders ?? [])
                .Select(f => RemotePath.TryFolder(f, out var rel) ? rel : null)
                .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            return s with { PhotoFolders = folders.Count == 0 ? ["DCIM/Camera"] : folders };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(); // a broken settings file must never stop the app
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
