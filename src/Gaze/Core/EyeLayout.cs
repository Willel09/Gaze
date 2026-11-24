using System;

namespace Gaze.Core;

/// <summary>One eye in image pixels. Tilt is degrees, clockwise.</summary>
public readonly record struct EyePose(double Cx, double Cy, double Width, double Height, double IrisR, double TiltDeg);

public static class EyeLayout
{
    /// <summary>Left/right poses in image pixels. "Left" is whichever eye is further left in the image.</summary>
    public static (EyePose Left, EyePose Right) Compute(EyeSettings s, double imgW, double imgH)
    {
        double ax = s.LeftX * imgW, ay = s.LeftY * imgH, bx = s.RightX * imgW, by = s.RightY * imgH;
        if (bx < ax) { (ax, bx) = (bx, ax); (ay, by) = (by, ay); }

        var ipd = Math.Max(4, Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)));
        var tilt = Math.Atan2(by - ay, bx - ax) * 180.0 / Math.PI + s.Tilt;

        EyePose Make(double x, double y) =>
            new(x, y, s.EyeWidth * ipd, s.EyeHeight * ipd, s.IrisSize * ipd, tilt);

        return (Make(ax, ay), Make(bx, by));
    }

    /// <summary>The eyes to draw: two normally, one in cyclops mode.</summary>
    public static EyePose[] Poses(EyeSettings s, double imgW, double imgH)
    {
        if (!s.Cyclops)
        {
            var (a, b) = Compute(s, imgW, imgH);
            return new[] { a, b };
        }
        var unit = Math.Max(4, s.CyclopsSize * imgW);
        return new[] { new EyePose(s.LeftX * imgW, s.LeftY * imgH, s.EyeWidth * unit, s.EyeHeight * unit, s.IrisSize * unit, s.Tilt) };
    }

    /// <summary>Reach of the pupil inside the eye opening, in the eye's own (tilted) frame.</summary>
    public static (double Rx, double Ry) Range(EyePose p, double range) =>
        (Math.Max(0, p.Width / 2 - 0.35 * p.IrisR) * range, p.Height / 2 * 0.55 * range);
}
