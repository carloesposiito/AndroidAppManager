using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AndroidAppManager.App;

/// <summary>Switches the native title bar to the dark variant (Windows 10 2004+). Silently does nothing elsewhere.</summary>
internal static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20, UseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window) => window.SourceInitialized += (_, _) =>
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var on = 1;
        if (DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, UseImmersiveDarkModeBefore20H1, ref on, sizeof(int));
    };
}
