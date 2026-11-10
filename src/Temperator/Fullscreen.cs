using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Temperator;

/// <summary>Un jeu ou une vidéo occupe-t-il tout l'écran où se trouve le widget ?</summary>
static class Fullscreen
{
    public static bool OnWidgetScreen(IntPtr widget)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == widget) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;   // le plan du PC, les réglages…

        var cls = new StringBuilder(128);
        GetClassName(fg, cls, cls.Capacity);
        switch (cls.ToString())
        {
            case "Progman": case "WorkerW":                                  // bureau
            case "Shell_TrayWnd": case "Shell_SecondaryTrayWnd":            // barre des tâches
            case "Windows.UI.Core.CoreWindow": case "XamlExplorerHostIslandWindow":   // menu Démarrer, recherche
            case "MultitaskingViewFrame": case "ForegroundStaging":          // Alt+Tab, Win+Tab
                return false;
        }

        var monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        if (monitor != MonitorFromWindow(widget, MONITOR_DEFAULTTONEAREST)) return false;   // plein écran sur un autre écran
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(fg, out var r)) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
