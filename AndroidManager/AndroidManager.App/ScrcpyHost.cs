using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AndroidManager.App;

/// <summary>
/// Hosts scrcpy's own window inside a WPF panel: a plain child window of ours is created, and the scrcpy
/// window is re-parented into it, stripped of its frame, and kept sized to the panel.
/// </summary>
public sealed class ScrcpyHost : HwndHost
{
    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000, SS_BLACKRECT = 0x0004;
    private const long WS_POPUP = 0x80000000L, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000,
        WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    private const int GWL_STYLE = -16;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;

    private IntPtr _host;
    private IntPtr _child;

    /// <summary>Finds a top-level window by its exact title, or zero.</summary>
    public static IntPtr FindByTitle(string title) => FindWindow(null, title);

    public bool HasChild => _child != IntPtr.Zero && IsWindow(_child);

    /// <summary>Moves the scrcpy window into this host. Call on the UI thread, after the host exists.</summary>
    public bool Attach(IntPtr window)
    {
        if (_host == IntPtr.Zero || window == IntPtr.Zero) return false;
        _child = window;
        SetParent(window, _host);
        var style = GetWindowLongPtr(window, GWL_STYLE).ToInt64();
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowLongPtr(window, GWL_STYLE, new IntPtr(style));
        Fit(SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        return true;
    }

    /// <summary>Forgets the scrcpy window (it closed or was stopped). The window itself is not touched.</summary>
    public void Detach()
    {
        _child = IntPtr.Zero;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _host = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS | SS_BLACKRECT,
            0, 0, 0, 0, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, _host);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _child = IntPtr.Zero;
        DestroyWindow(hwnd.Handle);
        _host = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        Fit(SWP_NOACTIVATE);
    }

    /// <summary>The scrcpy window fills the panel; scrcpy itself keeps the phone screen's proportions inside it.</summary>
    private void Fit(uint flags)
    {
        if (!HasChild || _host == IntPtr.Zero) return;
        GetClientRect(_host, out var rc);
        SetWindowPos(_child, IntPtr.Zero, 0, 0, Math.Max(rc.Right, 1), Math.Max(rc.Bottom, 1), SWP_NOZORDER | flags);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
