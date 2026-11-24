using System;

namespace Gaze.Core;

/// <summary>Pure math, no UI types, so it can be unit tested on any OS.</summary>
public static class GazeMath
{
    /// <summary>
    /// Pupil offset inside an eye-opening ellipse (rx, ry) for a cursor vector (dx, dy) measured from the eye.
    /// tanh gives a soft approach to the limit: near cursor = small move, far cursor = full range, never beyond.
    /// </summary>
    public static (double X, double Y) Target(double dx, double dy, double rx, double ry, double reach)
    {
        var d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-9 || reach <= 0) return (0, 0);
        var t = Math.Tanh(d / reach);
        return (dx / d * t * rx, dy / d * t * ry);
    }

    /// <summary>Critically damped spring (same maths as Unity's SmoothDamp): smooth, no overshoot, slightly delayed.</summary>
    public static void SmoothDamp(ref double pos, ref double vel, double target, double smoothTime, double dt)
    {
        smoothTime = Math.Max(0.0001, smoothTime);
        var omega = 2.0 / smoothTime;
        var x = omega * dt;
        var exp = 1.0 / (1.0 + x + 0.48 * x * x + 0.235 * x * x * x);
        var change = pos - target;
        var temp = (vel + omega * change) * dt;
        vel = (vel - omega * temp) * exp;
        pos = target + (change + temp) * exp;
    }

    /// <summary>Smoothness slider 0..1 to spring time in seconds (0.04 = snappy, 0.30 = lazy).</summary>
    public static double SmoothTime(double smoothness) => 0.04 + 0.26 * Math.Clamp(smoothness, 0, 1);

    public static (double X, double Y) Rotate(double x, double y, double degrees)
    {
        var r = degrees * Math.PI / 180.0;
        var c = Math.Cos(r);
        var s = Math.Sin(r);
        return (x * c - y * s, x * s + y * c);
    }
}
