using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Gaze.Core;

namespace Gaze.Rendering;

/// <summary>A small stylised eye for the app's own chrome (logo, empty state) that looks at the cursor.</summary>
public sealed class MiniEye
{
    readonly TranslateTransform _t = new();
    readonly double _rx, _ry;
    double _x, _y, _vx, _vy;
    readonly Point _center;

    public MiniEye(Canvas host)
    {
        var s = host.Width;
        double w = s * 0.94, h = s * 0.58, r = s * 0.2;
        _center = new Point(s / 2, s / 2);
        _rx = w / 2 - r * 0.85; _ry = h / 2 * 0.45;

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(-w / 2, 0), true, true);
            c.QuadraticBezierTo(new Point(0, -h), new Point(w / 2, 0), true, false);
            c.QuadraticBezierTo(new Point(0, h), new Point(-w / 2, 0), true, false);
        }
        g.Freeze();

        var eye = new Canvas { Clip = g };
        Canvas.SetLeft(eye, _center.X); Canvas.SetTop(eye, _center.Y);

        eye.Children.Add(new Path { Data = g, Fill = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF4)) });

        var layer = new Canvas { RenderTransform = _t };
        var iris = new Ellipse
        {
            Width = r * 2, Height = r * 2,
            Fill = new RadialGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(0x4D, 0xA3, 0xFF), 0.0), new(Color.FromRgb(0x0A, 0x71, 0xE3), 0.6), new(Color.FromRgb(0x06, 0x2B, 0x57), 1.0),
            }),
        };
        Canvas.SetLeft(iris, -r); Canvas.SetTop(iris, -r);
        var pupil = new Ellipse { Width = r * 0.85, Height = r * 0.85, Fill = new SolidColorBrush(Color.FromRgb(8, 8, 9)) };
        Canvas.SetLeft(pupil, -r * 0.425); Canvas.SetTop(pupil, -r * 0.425);
        var glint = new Ellipse { Width = r * 0.42, Height = r * 0.42, Fill = Brushes.White, Opacity = 0.9 };
        Canvas.SetLeft(glint, -r * 0.55); Canvas.SetTop(glint, -r * 0.6);
        layer.Children.Add(iris); layer.Children.Add(pupil); layer.Children.Add(glint);
        eye.Children.Add(layer);

        eye.Children.Add(new Path { Data = g, Stroke = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), StrokeThickness = 1.2 });
        host.Children.Add(eye);
    }

    /// <summary>cursor is in the host canvas' coordinate space.</summary>
    public void Update(Point cursor, double dt)
    {
        var (tx, ty) = GazeMath.Target(cursor.X - _center.X, cursor.Y - _center.Y, _rx, _ry, 220);
        GazeMath.SmoothDamp(ref _x, ref _vx, tx, 0.1, dt);
        GazeMath.SmoothDamp(ref _y, ref _vy, ty, 0.1, dt);
        if (Math.Abs(_vx) + Math.Abs(_vy) > 0.01 || Math.Abs(_x - tx) + Math.Abs(_y - ty) > 0.05) { _t.X = _x; _t.Y = _y; }
    }
}
