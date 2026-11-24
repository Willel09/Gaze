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
/// Puts one borderless child window per monitor into the desktop's wallpaper layer, behind the icons.
/// Windows builds stack that layer differently, so several attach methods exist. The first one is used right away;
/// the moment the desktop is actually visible a magenta test square is painted and read back from the screen.
/// If it is not there, the next method is tried, until one is proven to show. That one is remembered.
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

    readonly record struct Strategy(string Name, bool Progman, bool Late, ZSlot Z);

    static readonly Strategy[] All =
    {
        new("WorkerW, child", false, false, ZSlot.Default),
        new("WorkerW, late parent", false, true, ZSlot.Default),
        new("Progman, under icons", true, false, ZSlot.UnderIcons),
        new("Progman, under icons, late parent", true, true, ZSlot.UnderIcons),
        new("Progman, above worker", true, false, ZSlot.AboveWorker),
        new("Progman, bottom", true, false, ZSlot.Bottom),
        new("Progman, top (covers icons)", true, false, ZSlot.Top),
    };

    readonly List<Overlay> _overlays = new();
    readonly DispatcherTimer _probe = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _nudge = new() { Interval = TimeSpan.FromMilliseconds(900) };
    List<(MonitorRect Rect, BitmapSource Bmp)> _frames = new();
    Strategy[] _order = All;
    int _idx;
    bool _verified, _clocked, _locked, _wasDown;
    int _nudges;
    string? _path;
    EyeSettings? _eyes;
    string? _preferred;

    public event Action? ActiveChanged;
    /// <summary>Raised once a method has been proven to show on screen. Argument is its name.</summary>
    public event Action<string>? Verified;
    public bool Running => _overlays.Count > 0;
    public bool AnyActive { get; private set; }
    public string? Error { get; private set; }
    public string? Method => _verified ? _order[_idx].Name : null;

    public WallpaperHost()
    {
        _probe.Tick += (_, _) => Probe();
        // Explorer may repaint the wallpaper after it changes and cover a child window that has nothing new to draw.
        _nudge.Tick += (_, _) => { Redraw(); if (++_nudges >= 3) _nudge.Stop(); };
    }

    public bool Start(string imagePath, EyeSettings eyes, string? preferredMethod = null)
    {
        Stop();
        _path = imagePath; _eyes = eyes; _preferred = preferredMethod; Error = null; _verified = false;
        Log.Write($"start os={Environment.OSVersion.Version} image={System.IO.Path.GetFileName(imagePath)} preferred={preferredMethod ?? "-"}");

        try
        {
            var (iw, ih) = ImageLoader.Probe(imagePath);
            var cache = new Dictionary<int, BitmapSource>();
            _frames = new();
            foreach (var m in DesktopWindows.Monitors())
            {
                var scale = Math.Max((double)m.W / iw, (double)m.H / ih);
                var decodeW = scale < 1 ? (int)Math.Ceiling(iw * scale) : 0;
                if (!cache.TryGetValue(decodeW, out var bmp)) cache[decodeW] = bmp = ImageLoader.Load(imagePath, decodeW);
                _frames.Add((m, bmp));
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Write("image failed: " + ex);
            return false;
        }

        try { Native.SetThreadDpiHostingBehavior(1); } catch { /* Windows 10 before 1803 */ }

        // The method that worked last time goes first.
        var order = new List<Strategy>(All);
        var pref = order.FindIndex(s => s.Name == preferredMethod);
        if (pref > 0) { var s = order[pref]; order.RemoveAt(pref); order.Insert(0, s); }
        _order = order.ToArray();
        _idx = -1;

        if (!NextStrategy()) return false;
        _probe.Start();
        Probe();
        return true;
    }

    /// <summary>Tears down the current attempt and attaches with the next method that can at least create its windows.</summary>
    bool NextStrategy()
    {
        DisposeOverlays();
        while (++_idx < _order.Length)
        {
            var s = _order[_idx];
            try
            {
                var layers = DesktopWindows.Find();
                if (_idx == 0 || _verified == false && _idx <= 1) Log.Write("desktop tree: " + DesktopWindows.Describe(layers));
                var parent = s.Progman ? layers.Progman : DesktopWindows.WorkerParent(layers);
                if (parent == IntPtr.Zero) { Log.Write($"skip '{s.Name}': no parent window"); continue; }

                foreach (var (rect, bmp) in _frames) _overlays.Add(Create(rect, bmp, _eyes!, s, parent, layers));
                Log.Write($"attached '{s.Name}' parent={DesktopWindows.Fmt(parent)}");
                foreach (var o in _overlays)
                    Log.Write($"  overlay {o.Source.Handle} parentOk={Native.GetParent(o.Source.Handle) == parent} prev={DesktopWindows.Fmt(Native.GetWindow(o.Source.Handle, Native.GW_HWNDPREV))} next={DesktopWindows.Fmt(Native.GetWindow(o.Source.Handle, Native.GW_HWNDNEXT))}");
                _nudges = 0; _nudge.Stop(); _nudge.Start();
                return true;
            }
            catch (Exception ex)
            {
                Log.Write($"'{s.Name}' failed: {ex.Message}");
                DisposeOverlays();
            }
        }
        Error = "Windows did not show the eyes on the desktop. Open the log file for details.";
        Log.Write(Error);
        Stop();
        return false;
    }

    Overlay Create(MonitorRect m, BitmapSource bmp, EyeSettings eyes, Strategy s, IntPtr parent, DesktopWindows.Layers layers)
    {
        var pt = new Native.POINT { X = m.X, Y = m.Y };
        if (!s.Late) Native.ScreenToClient(parent, ref pt);

        var p = new HwndSourceParameters("GazeWallpaper")
        {
            WindowStyle = s.Late
                ? Native.WS_POPUP | Native.WS_VISIBLE
                : Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS | Native.WS_CLIPCHILDREN,
            ExtendedWindowStyle = Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW,
            ParentWindow = s.Late ? IntPtr.Zero : parent,
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
            Native.SetParent(src.Handle, parent);
            var rel = new Native.POINT { X = m.X, Y = m.Y };
            Native.ScreenToClient(parent, ref rel);
            Native.SetWindowPos(src.Handle, IntPtr.Zero, rel.X, rel.Y, m.W, m.H, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_FRAMECHANGED);
        }

        var after = s.Z switch
        {
            ZSlot.UnderIcons => layers.DefViewInProgman,
            ZSlot.AboveWorker => layers.WorkerInProgman,
            ZSlot.Bottom => Native.HWND_BOTTOM,
            ZSlot.Top => Native.HWND_TOP,
            _ => IntPtr.Zero,
        };
        if (s.Z != ZSlot.Default && (after != IntPtr.Zero || s.Z == ZSlot.Top))
            Native.SetWindowPos(src.Handle, after, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        return new Overlay { Source = src, Scene = scene, Rect = m };
    }

    static IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_NCHITTEST) { handled = true; return new IntPtr(Native.HTTRANSPARENT); }
        if (msg == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(Native.MA_NOACTIVATE); }
        return IntPtr.Zero;
    }

    bool IsOurs(IntPtr h)
    {
        foreach (var o in _overlays)
            if (o.Source.Handle == h || Native.IsChild(o.Source.Handle, h)) return true;
        return false;
    }

    /// <summary>Paints a magenta square over this eye and reads the screen back. Only meaningful while the eye is on a visible desktop.</summary>
    bool VerifyAt(Overlay o, Point eye)
    {
        int px = o.Rect.X + (int)eye.X, py = o.Rect.Y + (int)eye.Y;
        Log.Write($"  verify at {px},{py}: hit {DesktopWindows.DescribeAt(px, py)}");
        o.Scene.ShowTestPatch(eye, true);
        var seen = false;
        for (var i = 0; i < 8 && !seen; i++)
        {
            Pump();
            Thread.Sleep(90);
            seen = IsMagenta(px, py);
        }
        o.Scene.ShowTestPatch(eye, false);
        Pump();
        return seen;
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

    /// <summary>Re-checks which eyes can be seen, proves the attach method once a desktop spot is visible, and re-attaches if Explorer restarted.</summary>
    void Probe()
    {
        if (_overlays.Count == 0) return;
        if (!Native.IsWindow(Native.GetParent(_overlays[0].Source.Handle)) && _path != null && _eyes != null)
        {
            Log.Write("parent window lost, re-attaching");
            Start(_path, _eyes, _verified ? _order[_idx].Name : _preferred);
            return;
        }

        var any = false;
        Overlay? probeO = null; Point probeP = default;
        foreach (var o in _overlays)
        {
            var active = false;
            if (!_locked)
                foreach (var c in o.Scene.EyeCenters())
                    if (DesktopWindows.IsDesktopAt(o.Rect.X + (int)c.X, o.Rect.Y + (int)c.Y, IsOurs))
                    {
                        active = true;
                        if (probeO == null) { probeO = o; probeP = c; }
                    }
            o.Active = active;
            any |= active;
        }

        if (!_verified && probeO != null)
        {
            var ok = VerifyAt(probeO, probeP);
            Log.Write($"  '{_order[_idx].Name}' self-test: {(ok ? "visible" : "NOT visible")}");
            if (ok)
            {
                _verified = true;
                Verified?.Invoke(_order[_idx].Name);
            }
            else
            {
                if (NextStrategy()) { _probe.Start(); Probe(); }
                ActiveChanged?.Invoke();
                return;
            }
        }

        if (any != AnyActive)
        {
            AnyActive = any;
            if (any) Redraw();
            Log.Write(any ? "desktop visible: eyes active" : "desktop covered: eyes paused");
            ActiveChanged?.Invoke();
        }
        if (any && !_clocked) { FrameClock.Add(Tick); _clocked = true; }
        else if (!any && _clocked) { FrameClock.Remove(Tick); _clocked = false; }
    }

    void Tick(double dt)
    {
        if (!Native.GetCursorPos(out var c)) return;
        var down = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;
        if (down && !_wasDown && _eyes?.ClickBlink == true)
            foreach (var o in _overlays) if (o.Active) o.Scene.TriggerBlink();
        _wasDown = down;
        foreach (var o in _overlays)
            if (o.Active) o.Scene.Update(new Point(c.X - o.Rect.X, c.Y - o.Rect.Y), dt);
    }

    void DisposeOverlays()
    {
        if (_clocked) { FrameClock.Remove(Tick); _clocked = false; }
        foreach (var o in _overlays) { try { o.Source.RootVisual = null; o.Source.Dispose(); } catch { /* already gone */ } }
        _overlays.Clear();
    }

    public void Stop()
    {
        _probe.Stop(); _nudge.Stop();
        DisposeOverlays();
        if (AnyActive) { AnyActive = false; ActiveChanged?.Invoke(); }
    }

    public void Dispose() => Stop();
}
