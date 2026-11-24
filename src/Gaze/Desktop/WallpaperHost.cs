using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Gaze.Core;
using Gaze.Rendering;

namespace Gaze.Desktop;

/// <summary>
/// Puts one borderless child window per monitor into the desktop's wallpaper layer. They sit behind the icons,
/// never take the mouse, and every normal window covers them, so the eyes are part of the wallpaper.
/// Windows builds differ in how that layer is built, so several attach methods are tried and each one is verified
/// by painting a test square and reading the screen back. The first that really shows is kept.
/// A cheap 4 Hz probe checks whether an eye is visible; only then does the 60 fps loop run.
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

    readonly record struct Strategy(string Name, bool Progman, bool Late);

    static readonly Strategy[] Strategies =
    {
        new("WorkerW, child at creation", false, false),
        new("WorkerW, parented afterwards", false, true),
        new("Progman, child at creation", true, false),
        new("Progman, parented afterwards", true, true),
    };

    readonly List<Overlay> _overlays = new();
    readonly DispatcherTimer _probe = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _nudge = new() { Interval = TimeSpan.FromMilliseconds(900) };
    IntPtr _parent, _insertAfter;
    bool _clocked, _locked;
    int _nudges;
    string? _path;
    EyeSettings? _eyes;

    public event Action? ActiveChanged;
    public bool Running => _overlays.Count > 0;
    public bool AnyActive { get; private set; }
    public string? Error { get; private set; }
    public string? Method { get; private set; }

    public WallpaperHost()
    {
        _probe.Tick += (_, _) => Probe();
        // Explorer may repaint the wallpaper after it changes and cover a child window that has nothing new to draw.
        _nudge.Tick += (_, _) => { Redraw(); if (++_nudges >= 3) _nudge.Stop(); };
    }

    public bool Start(string imagePath, EyeSettings eyes)
    {
        Stop();
        _path = imagePath; _eyes = eyes; Error = null; Method = null;
        Log.Write($"start os={Environment.OSVersion.Version} image={System.IO.Path.GetFileName(imagePath)}");

        List<(MonitorRect Rect, BitmapSource Bmp)> frames;
        try
        {
            var (iw, ih) = ImageLoader.Probe(imagePath);
            var cache = new Dictionary<int, BitmapSource>();
            frames = new();
            foreach (var m in DesktopWindows.Monitors())
            {
                var scale = Math.Max((double)m.W / iw, (double)m.H / ih);
                var decodeW = scale < 1 ? (int)Math.Ceiling(iw * scale) : 0;
                if (!cache.TryGetValue(decodeW, out var bmp)) cache[decodeW] = bmp = ImageLoader.Load(imagePath, decodeW);
                frames.Add((m, bmp));
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Write("image failed: " + ex);
            return false;
        }

        try { Native.SetThreadDpiHostingBehavior(1); } catch { /* Windows 10 before 1803 */ }

        foreach (var s in Strategies)
        {
            try
            {
                _parent = DesktopWindows.FindWallpaperParent(s.Progman, out _insertAfter);
                Log.Write($"try '{s.Name}': parent={_parent} class={Native.ClassName(_parent)} insertAfter={_insertAfter}");
                if (_parent == IntPtr.Zero) continue;

                foreach (var (rect, bmp) in frames) _overlays.Add(Create(rect, bmp, eyes, s));
                var ok = SelfTest();
                Log.Write($"  self-test: {(ok == null ? "no free desktop spot, assuming it works" : ok.ToString())}");
                if (ok != false)
                {
                    Method = s.Name;
                    _probe.Start();
                    Probe();
                    _nudges = 0; _nudge.Stop(); _nudge.Start();
                    return true;
                }
            }
            catch (Exception ex) { Log.Write($"  failed: {ex.Message}"); }
            DisposeOverlays();
        }

        Error = "Windows did not show the eyes on the desktop. Open the log file for details.";
        Log.Write(Error);
        return false;
    }

    Overlay Create(MonitorRect m, BitmapSource bmp, EyeSettings eyes, Strategy s)
    {
        var pt = new Native.POINT { X = m.X, Y = m.Y };
        if (!s.Late) Native.ScreenToClient(_parent, ref pt);

        var p = new HwndSourceParameters("GazeWallpaper")
        {
            WindowStyle = s.Late
                ? Native.WS_POPUP | Native.WS_VISIBLE
                : Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS | Native.WS_CLIPCHILDREN,
            ExtendedWindowStyle = Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW,
            ParentWindow = s.Late ? IntPtr.Zero : _parent,
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

        if (s.Late)
        {
            Native.SetWindowLongPtr(src.Handle, Native.GWL_STYLE, new IntPtr(Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS | Native.WS_CLIPCHILDREN));
            Native.SetParent(src.Handle, _parent);
            var rel = new Native.POINT { X = m.X, Y = m.Y };
            Native.ScreenToClient(_parent, ref rel);
            Native.SetWindowPos(src.Handle, IntPtr.Zero, rel.X, rel.Y, m.W, m.H, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_FRAMECHANGED);
        }
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

    /// <summary>
    /// Paints a magenta square on a bit of desktop that no app is covering and reads the screen back.
    /// true = visible, false = not visible, null = no free desktop spot to test on.
    /// </summary>
    bool? SelfTest()
    {
        foreach (var o in _overlays)
        for (var fy = 0.12; fy < 0.9; fy += 0.17)
        for (var fx = 0.12; fx < 0.9; fx += 0.17)
        {
            int px = o.Rect.X + (int)(fx * o.Rect.W), py = o.Rect.Y + (int)(fy * o.Rect.H);
            if (!DesktopWindows.IsDesktopAt(px, py)) continue;

            o.Scene.ShowTestPatch(new Point(fx * o.Rect.W, fy * o.Rect.H), true);
            var seen = false;
            for (var i = 0; i < 8 && !seen; i++)
            {
                Pump();
                Thread.Sleep(90);
                seen = IsMagenta(px, py);
            }
            o.Scene.ShowTestPatch(new Point(fx * o.Rect.W, fy * o.Rect.H), false);
            Pump();
            return seen;
        }
        return null;
    }

    static void Pump()
    {
        var d = Dispatcher.CurrentDispatcher;
        d.Invoke(DispatcherPriority.Render, new Action(() => { }));
        d.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
    }

    static bool IsMagenta(int x, int y)
    {
        var dc = Native.GetDC(IntPtr.Zero);
        try
        {
            var c = Native.GetPixel(dc, x, y); // 0x00BBGGRR
            int r = (int)(c & 0xFF), g = (int)((c >> 8) & 0xFF), b = (int)((c >> 16) & 0xFF);
            return r > 225 && g < 30 && b > 225;
        }
        finally { Native.ReleaseDC(IntPtr.Zero, dc); }
    }

    void Redraw()
    {
        foreach (var o in _overlays)
            Native.RedrawWindow(o.Source.Handle, IntPtr.Zero, IntPtr.Zero, Native.RDW_INVALIDATE | Native.RDW_ALLCHILDREN | Native.RDW_UPDATENOW);
    }

    public void SetLocked(bool locked) { _locked = locked; Probe(); }

    /// <summary>Re-checks which eyes can be seen. Also re-attaches if Explorer restarted.</summary>
    void Probe()
    {
        if (_overlays.Count == 0) return;
        if (!Native.IsWindow(_parent) && _path != null && _eyes != null)
        {
            Log.Write("parent window lost, re-attaching");
            Start(_path, _eyes);
            return;
        }

        var any = false;
        foreach (var o in _overlays)
        {
            var active = false;
            if (!_locked)
                foreach (var c in o.Scene.EyeCenters())
                    active |= DesktopWindows.IsDesktopAt(o.Rect.X + (int)c.X, o.Rect.Y + (int)c.Y);
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

    void Tick(double dt)
    {
        if (!Native.GetCursorPos(out var c)) return;
        foreach (var o in _overlays)
            if (o.Active) o.Scene.Update(new Point(c.X - o.Rect.X, c.Y - o.Rect.Y), dt);
    }

    void DisposeOverlays()
    {
        foreach (var o in _overlays) { try { o.Source.RootVisual = null; o.Source.Dispose(); } catch { /* already gone */ } }
        _overlays.Clear();
    }

    public void Stop()
    {
        _probe.Stop(); _nudge.Stop();
        if (_clocked) { FrameClock.Remove(Tick); _clocked = false; }
        DisposeOverlays();
        if (AnyActive) { AnyActive = false; ActiveChanged?.Invoke(); }
    }

    public void Dispose() => Stop();
}
