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
    IntPtr _parent;
    bool _clocked;
    bool _locked;
    string? _path;
    EyeSettings? _eyes;

    public event Action? ActiveChanged;
    public bool Running => _overlays.Count > 0;
    public bool AnyActive { get; private set; }
    public string? Error { get; private set; }

    public WallpaperHost() { _probe.Tick += (_, _) => Probe(); }

    public bool Start(string imagePath, EyeSettings eyes)
    {
        Stop();
        _path = imagePath; _eyes = eyes; Error = null;
        try
        {
            _parent = DesktopWindows.FindWallpaperParent();
            if (_parent == IntPtr.Zero) { Error = "Could not find the desktop wallpaper layer."; return false; }
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
            Stop();
            return false;
        }
        _probe.Start();
        Probe();
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
            Start(_path, _eyes);
            return;
        }

        var any = false;
        foreach (var o in _overlays)
        {
            var active = false;
            if (!_locked)
            {
                var (l, r) = o.Scene.EyeCenters();
                active = IsVisible(o, l) || IsVisible(o, r);
            }
            o.Active = active;
            any |= active;
        }

        if (any != AnyActive)
        {
            AnyActive = any;
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
        _probe.Stop();
        if (_clocked) { FrameClock.Remove(Tick); _clocked = false; }
        foreach (var o in _overlays) { o.Source.RootVisual = null; o.Source.Dispose(); }
        _overlays.Clear();
        if (AnyActive) { AnyActive = false; ActiveChanged?.Invoke(); }
    }

    public void Dispose() => Stop();
}
