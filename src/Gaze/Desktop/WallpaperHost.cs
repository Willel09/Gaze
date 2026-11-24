using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gaze.Core;
using Gaze.Rendering;

namespace Gaze.Desktop;

/// <summary>
/// Puts one borderless child window per monitor into the desktop's wallpaper layer. They sit behind the icons,
/// are click-through, and are hidden by every normal window, so the eyes literally are part of the wallpaper.
/// A cheap 4 Hz probe checks whether an eye is actually visible; only then does the 60 fps loop run.
/// </summary>
public sealed class WallpaperHost : IDisposable
{
    sealed class Overlay
    {
        public HwndSource Source = null!;
        public WallpaperScene Scene = null!;
        public MonitorRect Rect;
        public bool Active;
    }

    readonly List<Overlay> _overlays = new();
    readonly System.Windows.Threading.DispatcherTimer _probe = new() { Interval = TimeSpan.FromMilliseconds(250) };
    IntPtr _parent, _insertAfter;
    bool _compat;
    readonly System.Windows.Threading.DispatcherTimer _nudge = new() { Interval = TimeSpan.FromMilliseconds(900) };
    int _nudges;
    bool _clocked;
    bool _locked;
    string? _path;
    EyeSettings? _eyes;

    public event Action? ActiveChanged;
    public bool Running => _overlays.Count > 0;
    public bool AnyActive { get; private set; }
    public string? Error { get; private set; }

    public WallpaperHost()
    {
        _probe.Tick += (_, _) => Probe();
        // Explorer may repaint the wallpaper after it changes and cover a child window that has nothing new to draw, so repaint a few times.
        _nudge.Tick += (_, _) =>
        {
            Redraw();
            if (++_nudges >= 3) { _nudge.Stop(); Diagnose(); }
        };
    }

    void Redraw()
    {
        foreach (var o in _overlays)
            Native.RedrawWindow(o.Source.Handle, IntPtr.Zero, IntPtr.Zero, Native.RDW_INVALIDATE | Native.RDW_ALLCHILDREN | Native.RDW_UPDATENOW);
    }

    void Diagnose()
    {
        Log.Write($"parent {_parent} class={Native.ClassName(_parent)} visible={Native.IsWindowVisible(_parent)}");
        foreach (var o in _overlays)
        {
            Native.GetWindowRect(o.Source.Handle, out var r);
            var cs = o.Scene.EyeCenters();
            var probe = cs.Length > 0 ? DescribeAt(o.Rect.X + (int)cs[0].X, o.Rect.Y + (int)cs[0].Y) : "-";
            Log.Write($"overlay {o.Source.Handle} visible={Native.IsWindowVisible(o.Source.Handle)} rect=({r.Left},{r.Top},{r.Right},{r.Bottom}) active={o.Active} firstEyeScreenHit={probe}");
        }
    }

    static string DescribeAt(int x, int y)
    {
        var h = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
        var root = h == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(h, Native.GA_ROOT);
        return $"{Native.ClassName(h)} (root {Native.ClassName(root)})";
    }

    public bool Start(string imagePath, EyeSettings eyes, bool compatibility = false)
    {
        Stop();
        _path = imagePath; _eyes = eyes; _compat = compatibility; Error = null;
        try
        {
            _parent = DesktopWindows.FindWallpaperParent(_compat, out _insertAfter);
            Log.Write($"start os={Environment.OSVersion.Version} compat={_compat} parent={_parent} class={Native.ClassName(_parent)} insertAfter={_insertAfter}");
            if (_parent == IntPtr.Zero) { Error = "Could not find the desktop wallpaper layer."; Log.Write(Error); return false; }
            try { Native.SetThreadDpiHostingBehavior(1); } catch { /* Windows 10 before 1803 */ }

            var (iw, ih) = ImageLoader.Probe(imagePath);
            var cache = new Dictionary<int, BitmapSource>();
            foreach (var m in DesktopWindows.Monitors())
            {
                var scale = Math.Max((double)m.W / iw, (double)m.H / ih);
                var decodeW = scale < 1 ? (int)Math.Ceiling(iw * scale) : 0;
                if (!cache.TryGetValue(decodeW, out var bmp)) cache[decodeW] = bmp = ImageLoader.Load(imagePath, decodeW);
                _overlays.Add(Create(m, bmp, eyes));
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Write("start failed: " + ex);
            Stop();
            return false;
        }
        _probe.Start();
        Probe();
        _nudges = 0; _nudge.Stop(); _nudge.Start();
        return true;
    }

    Overlay Create(MonitorRect m, BitmapSource bmp, EyeSettings eyes)
    {
        var pt = new Native.POINT { X = m.X, Y = m.Y };
        Native.ScreenToClient(_parent, ref pt);

        var p = new HwndSourceParameters("GazeWallpaper")
        {
            WindowStyle = Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS | Native.WS_CLIPCHILDREN,
            ExtendedWindowStyle = Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW,
            ParentWindow = _parent,
            PositionX = pt.X, PositionY = pt.Y, Width = m.W, Height = m.H,
            AdjustSizingForNonClientArea = false,
        };
        var src = new HwndSource(p);
        src.AddHook(Hook);

        var scene = new WallpaperScene();
        scene.Configure(bmp, eyes, m.W, m.H);
        // The scene is laid out in device pixels; undo the window's DPI scaling so 1 unit = 1 pixel.
        var dpi = src.CompositionTarget.TransformToDevice.M11;
        scene.LayoutTransform = new ScaleTransform(1 / dpi, 1 / dpi);
        src.RootVisual = scene;
        if (_insertAfter != IntPtr.Zero)
            Native.SetWindowPos(src.Handle, _insertAfter, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        return new Overlay { Source = src, Scene = scene, Rect = m };
    }

    static IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_NCHITTEST) { handled = true; return new IntPtr(Native.HTTRANSPARENT); }
        if (msg == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(Native.MA_NOACTIVATE); }
        return IntPtr.Zero;
    }

    public void SetLocked(bool locked) { _locked = locked; Probe(); }

    /// <summary>Re-checks which eyes can be seen. Also re-attaches if Explorer restarted.</summary>
    void Probe()
    {
        if (_overlays.Count == 0) return;
        if (!Native.IsWindow(_parent) && _path != null && _eyes != null)
        {
            Log.Write("parent window lost, re-attaching");
            Start(_path, _eyes, _compat);
            return;
        }

        var any = false;
        foreach (var o in _overlays)
        {
            var active = false;
            if (!_locked)
            {
                foreach (var c in o.Scene.EyeCenters()) active |= IsVisible(o, c);
            }
            o.Active = active;
            any |= active;
        }

        if (any != AnyActive)
        {
            AnyActive = any;
            if (any) Redraw();
            ActiveChanged?.Invoke();
        }
        if (any && !_clocked) { FrameClock.Add(Tick); _clocked = true; }
        else if (!any && _clocked) { FrameClock.Remove(Tick); _clocked = false; }
    }

    static bool IsVisible(Overlay o, Point sceneCenter) =>
        DesktopWindows.IsDesktopAt(o.Rect.X + (int)sceneCenter.X, o.Rect.Y + (int)sceneCenter.Y);

    void Tick(double dt)
    {
        if (!Native.GetCursorPos(out var c)) return;
        foreach (var o in _overlays)
            if (o.Active) o.Scene.Update(new Point(c.X - o.Rect.X, c.Y - o.Rect.Y), dt);
    }

    public void Stop()
    {
        _probe.Stop(); _nudge.Stop();
        if (_clocked) { FrameClock.Remove(Tick); _clocked = false; }
        foreach (var o in _overlays) { o.Source.RootVisual = null; o.Source.Dispose(); }
        _overlays.Clear();
        if (AnyActive) { AnyActive = false; ActiveChanged?.Invoke(); }
    }

    public void Dispose() => Stop();
}
