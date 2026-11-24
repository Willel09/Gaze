using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Gaze.Rendering;

/// <summary>A tongue that sticks out of a dark mouth opening toward a tip position. The origin is the mouth.</summary>
public sealed class TongueView : Canvas
{
    readonly Ellipse _hole = new() { Fill = new SolidColorBrush(Color.FromRgb(0x4A, 0x18, 0x26)) };
    readonly Canvas _arm = new();
    readonly Border _body = new()
    {
        Background = new LinearGradientBrush(Color.FromRgb(0xFF, 0x86, 0x9A), Color.FromRgb(0xE2, 0x4F, 0x6C), 90),
    };
    readonly Rectangle _line = new() { Fill = new SolidColorBrush(Color.FromArgb(0x90, 0xB8, 0x32, 0x52)) };
    readonly RotateTransform _rot = new();
    double _w = 10;

    public TongueView()
    {
        IsHitTestVisible = false;
        _arm.RenderTransform = _rot;
        _arm.Children.Add(_body); _arm.Children.Add(_line);
        Children.Add(_hole); Children.Add(_arm);
    }

    /// <summary>Put the mouth at m and give the tongue a thickness.</summary>
    public void Configure(Point m, double thickness)
    {
        _w = Math.Max(4, thickness);
        SetLeft(this, m.X); SetTop(this, m.Y);
        _hole.Width = _w * 1.5; _hole.Height = _w * 0.75;
        SetLeft(_hole, -_hole.Width / 2); SetTop(_hole, -_hole.Height / 2);
        _body.Height = _w; SetTop(_body, -_w / 2);
        _line.Height = Math.Max(1, _w * 0.07); SetTop(_line, -_line.Height / 2);
        SetTip(0, 0);
    }

    /// <summary>Point the tongue at tip (relative to the mouth). A tiny tip hides the tongue.</summary>
    public void SetTip(double x, double y)
    {
        var len = Math.Sqrt(x * x + y * y);
        _arm.Visibility = len < 2 ? Visibility.Collapsed : Visibility.Visible;
        if (len < 2) return;
        _rot.Angle = Math.Atan2(y, x) * 180 / Math.PI;
        _body.Width = len;
        _body.CornerRadius = new CornerRadius(0, _w / 2, _w / 2, 0);
        _line.Width = Math.Max(0, len - _w * 0.7);
        SetLeft(_line, 0);
    }
}
