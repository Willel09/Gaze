using System;
using System.Collections.Generic;
using System.Text;

namespace Gaze.Desktop;

public readonly record struct MonitorRect(int X, int Y, int W, int H);

/// <summary>Where, relative to the icon list (SHELLDLL_DefView) and the wallpaper WorkerW, an overlay is stacked.</summary>
internal enum ZSlot { Default, UnderIcons, AboveWorker, Bottom, Top }

/// <summary>Finds the window layer between the wallpaper and the desktop icons, and tells whether the desktop is what the user sees.</summary>
internal static class DesktopWindows
{
    public sealed record Layers(IntPtr Progman, IntPtr WorkerInProgman, IntPtr DefViewInProgman, IntPtr ClassicWorker);

    /// <summary>
    /// The "WorkerW" trick: asking Progman to spawn a WorkerW places a window behind the icons.
    /// Older Windows: a top-level WorkerW after the one that hosts the icons. Windows 11 24H2 and later:
    /// the icon list and a WorkerW are both children of Progman.
    /// </summary>
    public static Layers Find()
    {
        var progman = Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return new(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        Native.SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), Native.SMTO_NORMAL, 1000, out _);

        var defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        var inner = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);

        var classic = IntPtr.Zero;
        Native.EnumWindows((top, _) =>
        {
            if (Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                classic = Native.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            return true;
        }, IntPtr.Zero);

        return new(progman, inner, defView, classic);
    }

    public static IntPtr WorkerParent(Layers l) =>
        l.DefViewInProgman != IntPtr.Zero && l.WorkerInProgman != IntPtr.Zero ? l.WorkerInProgman
        : l.ClassicWorker != IntPtr.Zero ? l.ClassicWorker
        : l.WorkerInProgman;

    /// <summary>A text dump of the desktop window tree for the log.</summary>
    public static string Describe(Layers l)
    {
        var sb = new StringBuilder();
        sb.Append($"progman={Fmt(l.Progman)} defViewInProgman={Fmt(l.DefViewInProgman)} workerInProgman={Fmt(l.WorkerInProgman)} classicWorker={Fmt(l.ClassicWorker)}");
        if (l.Progman != IntPtr.Zero)
        {
            sb.Append(" | progman children top->bottom:");
            for (var c = Native.GetWindow(l.Progman, Native.GW_CHILD); c != IntPtr.Zero; c = Native.GetWindow(c, Native.GW_HWNDNEXT))
                sb.Append(' ').Append(Fmt(c));
        }
        var tops = new List<string>();
        Native.EnumWindows((top, _) =>
        {
            var cls = Native.ClassName(top);
            if (cls is "WorkerW" or "Progman") tops.Add(Fmt(top));
            return true;
        }, IntPtr.Zero);
        sb.Append(" | top-level desktop windows top->bottom: ").Append(string.Join(' ', tops));
        return sb.ToString();
    }

    public static string Fmt(IntPtr h)
    {
        if (h == IntPtr.Zero) return "0";
        Native.GetWindowRect(h, out var r);
        return $"{Native.ClassName(h)}#{h}[{(Native.IsWindowVisible(h) ? "vis" : "hid")} {r.Right - r.Left}x{r.Bottom - r.Top}@{r.Left},{r.Top}]";
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

    /// <summary>True when the pixel at this screen point belongs to the desktop (wallpaper, icons or our own overlay), not to an application.</summary>
    public static bool IsDesktopAt(int x, int y, Func<IntPtr, bool>? isOurs = null)
    {
        var h = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
        if (h == IntPtr.Zero) return false;
        if (isOurs != null && isOurs(h)) return true;
        var root = Native.GetAncestor(h, Native.GA_ROOT);
        var cls = Native.ClassName(root == IntPtr.Zero ? h : root);
        return cls is "Progman" or "WorkerW";
    }

    public static string DescribeAt(int x, int y)
    {
        var h = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
        var root = h == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(h, Native.GA_ROOT);
        return $"{Fmt(h)} root={Fmt(root)}";
    }
}
