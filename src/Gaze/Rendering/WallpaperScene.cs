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
        public double X, Y, Vx, Vy;
    }

    readonly Image _image = new();
    readonly EyeState[] _eyes = { new(), new() };
    EyeSettings _s = new();
    double _blinkT = -1, _nextBlink = 4;
    bool _ready;

    public WallpaperScene()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Background = Brushes.Black;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Children.Add(_image);
        foreach (var e in _eyes) Children.Add(e.View);
    }

    public bool Ready => _ready;

    /// <summary>Eye centers in scene pixels.</summary>
    public (Point Left, Point Right) EyeCenters() =>
        (new Point(_eyes[0].Pose.Cx, _eyes[0].Pose.Cy), new Point(_eyes[1].Pose.Cx, _eyes[1].Pose.Cy));

    public (EyePose Left, EyePose Right) Poses => (_eyes[0].Pose, _eyes[1].Pose);

    public bool EyesVisible
    {
        get => _eyes[0].View.Visibility == Visibility.Visible;
        set { foreach (var e in _eyes) e.View.Visibility = value ? Visibility.Visible : Visibility.Hidden; }
    }

    public void SetEyeOpacity(double o) { foreach (var e in _eyes) e.View.Opacity = o; }

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

        var (l, r) = EyeLayout.Compute(eyes, iw, ih);
        var poses = new[] { l, r };
        for (var i = 0; i < 2; i++)
        {
            var p = poses[i];
            var colors = ColorSampler.Sample(bmp, p);
            var sp = new EyePose(p.Cx * scale + ox, p.Cy * scale + oy, p.Width * scale, p.Height * scale, p.IrisR * scale, p.TiltDeg);
            var (rx, ry) = EyeLayout.Range(sp, eyes.Range);
            var st = _eyes[i];
            st.Pose = sp; st.Rx = rx; st.Ry = ry;
            st.View.Configure(sp, eyes.LidShadow, eyes.PupilSize, colors);
            st.View.Visibility = eyes.IsCalibrated ? Visibility.Visible : Visibility.Hidden;
        }
        _ready = eyes.IsCalibrated;
    }

    /// <summary>Advance one frame. cursor is in scene pixels. Returns true if anything moved (so callers can skip idle frames).</summary>
    public bool Update(Point cursor, double dt)
    {
        if (!_ready) return false;
        var reach = 0.25 * Math.Min(Width, Height) / Math.Max(0.1, _s.Sensitivity);
        var smooth = GazeMath.SmoothTime(_s.Smoothness);
        var moved = false;

        foreach (var e in _eyes)
        {
            var (lx, ly) = GazeMath.Rotate(cursor.X - e.Pose.Cx, cursor.Y - e.Pose.Cy, -e.Pose.TiltDeg);
            var (tx, ty) = GazeMath.Target(lx, ly, e.Rx, e.Ry, reach);
            GazeMath.SmoothDamp(ref e.X, ref e.Vx, tx, smooth, dt);
            GazeMath.SmoothDamp(ref e.Y, ref e.Vy, ty, smooth, dt);
            if (Math.Abs(e.Vx) + Math.Abs(e.Vy) > 0.02 || Math.Abs(e.X - tx) + Math.Abs(e.Y - ty) > 0.05)
            {
                e.View.SetOffset(e.X, e.Y);
                moved = true;
            }
        }
        return UpdateBlink(dt) | moved;
    }

    bool UpdateBlink(double dt)
    {
        if (!_s.Blink) return false;
        if (_blinkT < 0)
        {
            _nextBlink -= dt;
            if (_nextBlink > 0) return false;
            _blinkT = 0;
        }
        _blinkT += dt;
        const double dur = 0.17;
        var t = _blinkT / dur;
        var amount = t >= 1 ? 0 : 1 - Math.Abs(2 * t - 1); // close then open
        foreach (var e in _eyes) e.View.SetBlink(amount);
        if (t >= 1)
        {
            _blinkT = -1;
            _nextBlink = 2.8 + Random.Shared.NextDouble() * 4.5;
        }
        return true;
    }
}
