using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Temperator;

/// <summary>
/// « Au-dessus de tout, quoi qu'il arrive » : dès qu'une fenêtre d'une autre application passe devant le widget
/// (même une autre fenêtre « toujours au premier plan »), il repasse au-dessus. S'il est réduit (Win+M…), il revient.
/// Réagit à chaque changement de fenêtre active, et vérifie aussi toutes les 0,5 s.
/// </summary>
sealed class TopGuard
{
    readonly Window window;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly WinEventProc onForeground;   // gardé dans un champ : sinon le ramasse-miettes le libère
    readonly uint pid = (uint)Environment.ProcessId;
    IntPtr hook;

    /// <summary>Menu du widget ouvert : on ne passe pas devant ses propres menus.</summary>
    public bool Paused { get; set; }

    public TopGuard(Window window)
    {
        this.window = window;
        timer.Tick += (_, _) => Check();
        onForeground = (_, _, _, _, _, _, _) =>
        {
            Check();
            // la nouvelle fenêtre active se place parfois devant un peu après : on revérifie
            _ = window.Dispatcher.InvokeAsync(async () => { await Task.Delay(250); Check(); });
        };
        window.StateChanged += (_, _) => { if (timer.IsEnabled) Check(); };
    }

    public void Enable(bool on)
    {
        if (on == timer.IsEnabled) return;
        if (on)
        {
            timer.Start();
            hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, onForeground, 0, 0,
                                   WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            Check();
        }
        else
        {
            timer.Stop();
            if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook = IntPtr.Zero; }
        }
    }

    void Check()
    {
        if (Paused || !window.IsVisible) return;
        var me = new WindowInteropHelper(window).Handle;
        if (me == IntPtr.Zero) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!window.Topmost) window.Topmost = true;
        if (Covered(me))
            SetWindowPos(me, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>Une fenêtre visible d'une autre application est-elle au-dessus du widget, et le chevauche-t-elle ?</summary>
    bool Covered(IntPtr me)
    {
        if (!GetWindowRect(me, out var mine)) return false;
        int guardCount = 0;
        for (var h = GetWindow(me, GW_HWNDPREV); h != IntPtr.Zero && guardCount++ < 1024; h = GetWindow(h, GW_HWNDPREV))
        {
            if (!IsWindowVisible(h)) continue;
            GetWindowThreadProcessId(h, out uint owner);
            if (owner == pid) continue;   // nos menus et infobulles
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;   // fenêtres masquées par Windows
            if (GetWindowRect(h, out var r) && r.Left < mine.Right && r.Right > mine.Left && r.Top < mine.Bottom && r.Bottom > mine.Top)
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ Win32

    delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    const uint GW_HWNDPREV = 3;
    const uint EVENT_SYSTEM_FOREGROUND = 0x3, WINEVENT_OUTOFCONTEXT = 0x0, WINEVENT_SKIPOWNPROCESS = 0x2;
    const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
