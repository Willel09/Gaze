using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gaze.Core;

namespace Gaze.Rendering;

/// <summary>The photo (cover-fit) with two live eyes. Used by the desktop overlay and by the in-app preview.</summary>
public sealed class WallpaperScene : Canvas
{
    sealed class EyeState
    {
        public EyeView View = new();
        public EyePose Pose;
        public double Rx, Ry;
        public double X, Y, Vx, Vy, Focus = 1;
    }

    readonly Image _image = new();
    readonly TongueView _tongue = new() { Visibility = Visibility.Collapsed };
    bool _tongueOn;
    Point _mouth;
    double _tongueMax, _tx, _ty, _tvx, _tvy;
    readonly EyeState[] _eyes = { new(), new() };
    EyeSettings _s = new();
    double _blinkT = -1, _nextBlink = 4;
    double _idle, _wander, _sleep, _lid;
    Point _last, _fake;
    bool _ready;
    int _count = 2;

    public WallpaperScene()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Background = Brushes.Black;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Children.Add(_image);
        foreach (var e in _eyes) Children.Add(e.View);
        Children.Add(_tongue);
    }

    /// <summary>The area the tongue can reach, in scene pixels (empty when there is no tongue).</summary>
    public Rect TongueArea { get; private set; } = Rect.Empty;

    public bool Ready => _ready;

    /// <summary>Draw only the eyes on a transparent background; the real Windows wallpaper shows the photo.</summary>
    public bool EyesOnly
    {
        get => _image.Visibility != Visibility.Visible;
        set { _image.Visibility = value ? Visibility.Collapsed : Visibility.Visible; Background = value ? null : Brushes.Black; }
    }

    readonly System.Windows.Shapes.Rectangle _patch = new() { Width = 40, Height = 40, Fill = Brushes.Magenta, Visibility = Visibility.Collapsed };

    /// <summary>Solid magenta square used to check that the overlay really shows on the desktop.</summary>
    public void ShowTestPatch(Point center, bool on)
    {
        if (!Children.Contains(_patch)) Children.Add(_patch);
        SetLeft(_patch, center.X - 20); SetTop(_patch, center.Y - 20);
        SetZIndex(_patch, 1000);
        _patch.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Eye centers in scene pixels (one in cyclops mode).</summary>
    public Point[] EyeCenters() => Array.ConvertAll(Poses, p => new Point(p.Cx, p.Cy));

    public EyePose[] Poses { get; private set; } = Array.Empty<EyePose>();

    public bool EyesVisible
    {
        get => _eyes[0].View.Visibility == Visibility.Visible;
        set
        {
            for (var i = 0; i < _count; i++) _eyes[i].View.Visibility = value ? Visibility.Visible : Visibility.Hidden;
            _tongue.Visibility = value && _tongueOn ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public void SetEyeOpacity(double o) { foreach (var e in _eyes) e.View.Opacity = o; _tongue.Opacity = o; }

    /// <summary>Lay the photo out in a w x h scene (cover) and place the eyes. Cheap enough to call while dragging sliders.</summary>
    public void Configure(BitmapSource bmp, EyeSettings eyes, double w, double h)
    {
        _s = eyes;
        Width = w; Height = h;
        double iw = bmp.PixelWidth, ih = bmp.PixelHeight;
        var scale = Math.Max(w / iw, h / ih);
        double ox = (w - iw * scale) / 2, oy = (h - ih * scale) / 2;

        if (!ReferenceEquals(_image.Source, bmp)) _image.Source = bmp;
        SetLeft(_image, ox); SetTop(_image, oy);
        _image.Width = iw * scale; _image.Height = ih * scale;

        var poses = EyeLayout.Poses(eyes, iw, ih);
        var scenePoses = new EyePose[poses.Length];
        _count = poses.Length;
        for (var i = 0; i < 2; i++)
        {
            if (i >= poses.Length) { _eyes[i].View.Visibility = Visibility.Hidden; continue; }
            var p = poses[i];
            var colors = Override(ColorSampler.Sample(bmp, p), eyes);
            var sp = new EyePose(p.Cx * scale + ox, p.Cy * scale + oy, p.Width * scale, p.Height * scale, p.IrisR * scale, p.TiltDeg);
            var (rx, ry) = EyeLayout.Range(sp, eyes.Range);
            var st = _eyes[i];
            st.Pose = sp; st.Rx = rx; st.Ry = ry;
            scenePoses[i] = sp;
            st.View.Configure(sp, eyes, colors);
            st.View.Visibility = eyes.IsCalibrated ? Visibility.Visible : Visibility.Hidden;
        }
        Poses = scenePoses;
        _ready = eyes.IsCalibrated;

        _tongueOn = eyes.Tongue && eyes.IsCalibrated;
        if (_tongueOn)
        {
            var (mx, my, unit) = EyeLayout.Mouth(eyes, iw, ih);
            _mouth = new Point(mx * scale + ox, my * scale + oy);
            _tongueMax = Math.Max(8, eyes.TongueSize * unit * scale);
            _tongue.Configure(_mouth, _tongueMax * 0.36);
            TongueArea = new Rect(_mouth.X - _tongueMax, _mouth.Y - _tongueMax, _tongueMax * 2, _tongueMax * 2);
            _tx = _ty = _tvx = _tvy = 0;
        }
        else TongueArea = Rect.Empty;
        _tongue.Visibility = _tongueOn ? Visibility.Visible : Visibility.Collapsed;
    }

    static EyeColors Override(EyeColors c, EyeSettings s) =>
        c with { Iris = Parse(s.IrisColor) ?? c.Iris, Sclera = Parse(s.ScleraColor) ?? c.Sclera };

    static Color? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return null; }
    }

    /// <summary>Quick blink, used for mouse clicks.</summary>
    public void TriggerBlink() { if (_ready && _blinkT < 0) _blinkT = 0; }

    /// <summary>Advance one frame. cursor is in scene pixels. Returns true if anything moved (so callers can skip idle frames).</summary>
    public bool Update(Point cursor, double dt)
    {
        if (!_ready) return false;
        var reach = 0.25 * Math.Min(Width, Height) / Math.Max(0.1, _s.Sensitivity);
        var smooth = GazeMath.SmoothTime(_s.Smoothness);
        var moved = false;

        // Idle life: after a few seconds without mouse movement the eyes look around by themselves,
        // after a few minutes they fall asleep. Any movement wakes them.
        var target = cursor;
        var sleepGoal = 0.0;
        if (_s.IdleLife)
        {
            var dx = cursor.X - _last.X; var dy = cursor.Y - _last.Y;
            _last = cursor;
            if (dx * dx + dy * dy > 2.25) _idle = 0; else _idle += dt;

            if (_idle > 180) sleepGoal = 1;
            else if (_idle > 6)
            {
                _wander -= dt;
                if (_wander <= 0)
                {
                    var mx = (_eyes[0].Pose.Cx + _eyes[_count - 1].Pose.Cx) / 2;
                    var my = (_eyes[0].Pose.Cy + _eyes[_count - 1].Pose.Cy) / 2;
                    var ang = Random.Shared.NextDouble() * Math.PI * 2;
                    var r = reach * (0.25 + Random.Shared.NextDouble() * 1.1);
                    _fake = new Point(mx + Math.Cos(ang) * r, my + Math.Sin(ang) * r * 0.7);
                    _wander = 1.6 + Random.Shared.NextDouble() * 3.4;
                }
                target = _fake;
            }
        }
        else _idle = 0;

        for (var i = 0; i < _count; i++)
        {
            var e = _eyes[i];
            var (lx, ly) = GazeMath.Rotate(target.X - e.Pose.Cx, target.Y - e.Pose.Cy, -e.Pose.TiltDeg);
            if (_s.Crazy) { lx = -lx; ly = -ly; } // crazy mode: look away from the cursor
            var (tx, ty) = GazeMath.Target(lx, ly, e.Rx, e.Ry, reach);
            GazeMath.SmoothDamp(ref e.X, ref e.Vx, tx, smooth, dt);
            GazeMath.SmoothDamp(ref e.Y, ref e.Vy, ty, smooth, dt);
            if (Math.Abs(e.Vx) + Math.Abs(e.Vy) > 0.02 || Math.Abs(e.X - tx) + Math.Abs(e.Y - ty) > 0.05)
            {
                e.View.SetOffset(e.X, e.Y);
                moved = true;
            }

            // The pupil contracts a little when you come close, like focusing on something near.
            var close = Math.Clamp(1 - Math.Sqrt(lx * lx + ly * ly) / (reach * 0.7), 0, 1);
            var focus = 1 - 0.22 * close;
            if (Math.Abs(focus - e.Focus) > 0.004) { e.Focus = focus; e.View.SetPupilScale(focus); moved = true; }
        }

        _sleep += (sleepGoal - _sleep) * Math.Min(1, dt * (sleepGoal > _sleep ? 0.9 : 9));
        if (_sleep < 0.003) _sleep = 0;
        if (_tongueOn) moved |= UpdateTongue(target, reach, smooth, dt);
        return UpdateLid(dt) | moved;
    }

    /// <summary>The tongue points at the cursor and stretches further the further away it is. Crazy mode flips it too.</summary>
    bool UpdateTongue(Point target, double reach, double smooth, double dt)
    {
        double dx = target.X - _mouth.X, dy = target.Y - _mouth.Y;
        if (_s.Crazy) { dx = -dx; dy = -dy; }
        var d = Math.Sqrt(dx * dx + dy * dy);
        var ext = d < 1 ? 0 : _tongueMax * (0.4 + 0.6 * Math.Tanh(d / reach)) * (1 - _sleep);
        var gx = d < 1 ? 0 : dx / d * ext; var gy = d < 1 ? 0 : dy / d * ext;
        GazeMath.SmoothDamp(ref _tx, ref _tvx, gx, smooth * 1.4, dt);
        GazeMath.SmoothDamp(ref _ty, ref _tvy, gy, smooth * 1.4, dt);
        if (Math.Abs(_tvx) + Math.Abs(_tvy) < 0.02 && Math.Abs(_tx - gx) + Math.Abs(_ty - gy) < 0.05) return false;
        _tongue.SetTip(_tx, _ty);
        return true;
    }

    bool UpdateLid(double dt)
    {
        var blink = 0.0;
        if (_blinkT < 0 && _s.Blink)
        {
            _nextBlink -= dt;
            if (_nextBlink <= 0) _blinkT = 0;
        }
        if (_blinkT >= 0)
        {
            _blinkT += dt;
            const double dur = 0.17;
            var t = _blinkT / dur;
            blink = t >= 1 ? 0 : 1 - Math.Abs(2 * t - 1); // close then open
            if (t >= 1) { _blinkT = -1; _nextBlink = 2.8 + Random.Shared.NextDouble() * 4.5; }
        }
        var lid = Math.Max(blink, _sleep * 0.97);
        if (Math.Abs(lid - _lid) < 0.002) return false;
        _lid = lid;
        for (var i = 0; i < _count; i++) _eyes[i].View.SetBlink(lid);
        return true;
    }
}
