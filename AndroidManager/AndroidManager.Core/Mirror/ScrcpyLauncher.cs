namespace AndroidManager.Core.Mirror;

/// <summary>Command line for scrcpy 2.x. Arguments are passed as a list, so no shell quoting is involved.</summary>
public static class ScrcpyLauncher
{
    /// <summary>Unique title: the UI finds scrcpy's window by it, so two mirrors never get mixed up.</summary>
    public static string NewWindowTitle() => "AndroidManager-" + Guid.NewGuid().ToString("N")[..8];

    /// <param name="width">Window size in pixels = the panel it is embedded in. scrcpy draws the phone screen inside it
    /// with the right proportions (bars on the sides if needed). Omitted unless both are positive.</param>
    public static IReadOnlyList<string> Arguments(string serial, string windowTitle, int width = 0, int height = 0)
    {
        if (string.IsNullOrWhiteSpace(serial))
            throw new ArgumentException("Seriale mancante.", nameof(serial));
        // Borderless so it can live inside our window; no audio keeps it light and avoids extra permission prompts.
        List<string> args = ["--serial", serial.Trim(), "--window-title", windowTitle, "--window-borderless", "--no-audio"];
        if (width > 0 && height > 0) args.AddRange(["--window-width", width.ToString(), "--window-height", height.ToString()]);
        return args;
    }
}
