namespace AndroidManager.Core;

/// <summary>Where the app keeps its data: %AppData%\AndroidManager. Older installs used "AndroidAppManager"; that folder moves over once.</summary>
public static class AppDataLocation
{
    public const string Name = "AndroidManager";
    private const string OldName = "AndroidAppManager";

    public static string Root { get; } = Resolve(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    internal static string Resolve(string appData)
    {
        var current = Path.Combine(appData, Name);
        var old = Path.Combine(appData, OldName);
        if (!Directory.Exists(old) || Directory.Exists(current)) return current;
        try
        {
            Directory.Move(old, current);
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return old; // keep working from the old place rather than lose settings and backups
        }
    }
}
