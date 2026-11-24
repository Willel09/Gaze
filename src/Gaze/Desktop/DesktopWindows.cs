using System;
using System.Collections.Generic;

namespace Gaze.Desktop;

public readonly record struct MonitorRect(int X, int Y, int W, int H);

/// <summary>Finds the window that sits between the wallpaper and the desktop icons, and tells whether the desktop is what the user sees.</summary>
internal static class DesktopWindows
{
    /// <summary>
    /// The "WorkerW" trick: asking Progman to spawn a WorkerW places a window behind the icons.
    /// Windows 11 24H2 moved it inside Progman, so both layouts are handled.
    /// </summary>
    public static IntPtr FindWallpaperParent()
    {
        var progman = Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        Native.SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), Native.SMTO_NORMAL, 1000, out _);

        // 24H2 layout: SHELLDLL_DefView and WorkerW are both children of Progman.
        if (Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
        {
            var inner = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            if (inner != IntPtr.Zero) return inner;
        }

        // Classic layout: the WorkerW that follows the one hosting the icons.
        var found = IntPtr.Zero;
        Native.EnumWindows((top, _) =>
        {
            if (Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                found = Native.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static List<MonitorRect> Monitors()
    {
        var list = new List<MonitorRect>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr _, IntPtr __, ref Native.RECT r, IntPtr ___) =>
        {
            list.Add(new MonitorRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>True when the pixel at this screen point belongs to the desktop (wallpaper or icons), not to an application.</summary>
    public static bool IsDesktopAt(int x, int y)
    {
        var h = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
        if (h == IntPtr.Zero) return false;
        var root = Native.GetAncestor(h, Native.GA_ROOT);
        var cls = Native.ClassName(root == IntPtr.Zero ? h : root);
        return cls is "Progman" or "WorkerW";
    }
}
