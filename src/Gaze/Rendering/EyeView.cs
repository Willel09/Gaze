using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Gaze.Core;

namespace Gaze.Rendering;

/// <summary>
/// One synthetic eye drawn over the photo: sclera, iris with pupil and catch-light, lid shadow, optional blink lid.
/// Only the iris translate and the blink lid height change per frame, so rendering stays on the GPU.
/// </summary>
public sealed class EyeView : Canvas
{
    readonly Ellipse _sclera = new();
    readonly Canvas _irisLayer = new();
    readonly Ellipse _iris = new();
    readonly Ellipse _pupil = new();
    readonly Ellipse _glint = new();
    readonly Ellipse _vignette = new();
    readonly Rectangle _shadow = new();
    readonly Rectangle _lid = new();
    readonly Rectangle _lash = new();
    readonly TranslateTransform _shift = new();
    readonly RotateTransform _tilt = new();
    readonly EllipseGeometry _clip = new();
    readonly ScaleTransform _pupilScale = new(1, 1);
    double _h, _droop, _blink;

    public EyeView()
    {
        IsHitTestVisible = false;
        RenderTransform = _tilt;
        _irisLayer.RenderTransform = _shift;
        _glint.Opacity = 0.9;
        _pupil.RenderTransformOrigin = new Point(0.5, 0.5);
        _pupil.RenderTransform = _pupilScale;
        foreach (UIElement e in new UIElement[] { _sclera, _irisLayer, _vignette, _shadow, _lid, _lash })
            Children.Add(e);
        _irisLayer.Children.Add(_iris);
        _irisLayer.Children.Add(_pupil);
        _irisLayer.Children.Add(_glint);
        Clip = _clip;
    }

    public void Configure(EyePose p, EyeSettings st, EyeColors c)
    {
        Canvas.SetLeft(this, p.Cx);
        Canvas.SetTop(this, p.Cy);
        _tilt.Angle = p.TiltDeg;
        _h = p.Height;
        _droop = Math.Clamp(st.Droop, 0, 0.7);
        double w = p.Width, h = p.Height, r = p.IrisR;
        double lidShadow = st.LidShadow, pupilFrac = st.PupilSize;

        _clip.RadiusX = w / 2; _clip.RadiusY = h / 2;

        Place(_sclera, -w / 2 - 1, -h / 2 - 1, w + 2, h + 2);
        _sclera.Fill = Frozen(new RadialGradientBrush(
            Scale(c.Sclera, 1.04), Scale(c.Sclera, 0.74)) { RadiusX = 0.62, RadiusY = 0.7 });

        Place(_iris, -r, -r, 2 * r, 2 * r);
        var ring = Scale(c.Iris, 0.35);
        var mid = Scale(c.Iris, 1.0);
        var rim = Scale(c.Iris, 0.62);
        var stops = new[]
        {
            new GradientStop(Scale(c.Iris, 0.5), 0.0),
            new GradientStop(Scale(c.Iris, 0.78), Math.Min(0.7, pupilFrac)),
            new GradientStop(mid, Math.Min(0.8, pupilFrac + 0.2)),
            new GradientStop(rim, 0.88),
            new GradientStop(ring, 0.97),
            new GradientStop(Color.FromArgb(0, ring.R, ring.G, ring.B), 1.0),
        };
        _iris.Fill = Frozen(new RadialGradientBrush(new GradientStopCollection(stops)));

        var d = 2 * r * pupilFrac;
        var (pw, ph) = st.Pupil switch
        {
            PupilShape.Slit => (Math.Max(2, d * 0.34), r * 1.75),
            PupilShape.Horizontal => (r * 1.6, Math.Max(2, d * 0.5)),
            _ => (d, d),
        };
        Place(_pupil, -pw / 2, -ph / 2, pw, ph);
        _pupil.Fill = Frozen(new SolidColorBrush(Color.FromRgb(8, 8, 9)));
        _glint.Visibility = st.Glint ? Visibility.Visible : Visibility.Collapsed;

        var g = r * 0.42;
        Place(_glint, -r * 0.46, -r * 0.5, g, g);
        _glint.Fill = Frozen(new RadialGradientBrush(
            Color.FromArgb(235, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)));

        Place(_vignette, -w / 2, -h / 2, w, h);
        _vignette.Fill = Frozen(new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0, 0, 0, 0), 0.0),
            new(Color.FromArgb(0, 0, 0, 0), 0.62),
            new(Color.FromArgb(110, 20, 10, 8), 1.0),
        }));

        Place(_shadow, -w / 2, -h / 2, w, h);
        var a = (byte)(Math.Clamp(lidShadow, 0, 1) * 200);
        _shadow.Fill = Frozen(new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(a, 12, 6, 4), 0.0),
            new(Color.FromArgb((byte)(a / 3), 12, 6, 4), 0.32),
            new(Color.FromArgb(0, 12, 6, 4), 0.62),
        }, 90));

        _lid.Fill = Frozen(new SolidColorBrush(Scale(c.Skin, 0.92)));
        _lash.Fill = Frozen(new SolidColorBrush(Color.FromArgb(200, 18, 10, 8)));
        Place(_lid, -w / 2 - 1, -h / 2 - 1, w + 2, 0);
        Place(_lash, -w / 2 - 1, -h / 2, w + 2, Math.Max(1.2, h * 0.07));
        SetBlink(0);
        SetOffset(_shift.X, _shift.Y);
    }

    /// <summary>Iris offset in the eye's own tilted frame, in scene pixels.</summary>
    public void SetOffset(double x, double y) { _shift.X = x; _shift.Y = y; }

    /// <summary>0 = open, 1 = closed.</summary>
    public void SetBlink(double amount)
    {
        _blink = amount;
        var v = Math.Clamp(Math.Max(amount, _droop), 0, 1);
        _lid.Height = v * (_h + 2);
        _lash.Visibility = v > 0.02 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetTop(_lash, -_h / 2 + v * (_h + 2) - _lash.Height * 0.5);
    }

    /// <summary>1 = normal, smaller = pupil contracts while the cursor is close.</summary>
    public void SetPupilScale(double s) { _pupilScale.ScaleX = s; _pupilScale.ScaleY = s; }

    static void Place(FrameworkElement e, double x, double y, double w, double h)
    {
        Canvas.SetLeft(e, x); Canvas.SetTop(e, y); e.Width = w; e.Height = h;
    }

    static Color Scale(Color c, double f) => Color.FromRgb(
        (byte)Math.Clamp(c.R * f, 0, 255), (byte)Math.Clamp(c.G * f, 0, 255), (byte)Math.Clamp(c.B * f, 0, 255));

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
