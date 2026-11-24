using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Gaze.Core;

public readonly record struct EyeColors(Color Sclera, Color Iris, Color Skin);

/// <summary>Reads the photo around a marked eye so the replacement eye matches its lighting.</summary>
public static class ColorSampler
{
    static readonly EyeColors Fallback = new(Color.FromRgb(232, 228, 224), Color.FromRgb(96, 72, 52), Color.FromRgb(200, 160, 136));

    public static EyeColors Sample(BitmapSource img, EyePose p)
    {
        try { return SampleCore(img, p); }
        catch { return Fallback; }
    }

    static EyeColors SampleCore(BitmapSource img, EyePose p)
    {
        var ex = p.Width * 0.95;
        var ey = Math.Max(p.Height * 2.4, p.IrisR * 2.6);
        var x0 = (int)Math.Max(0, Math.Floor(p.Cx - ex));
        var y0 = (int)Math.Max(0, Math.Floor(p.Cy - ey));
        var x1 = (int)Math.Min(img.PixelWidth, Math.Ceiling(p.Cx + ex));
        var y1 = (int)Math.Min(img.PixelHeight, Math.Ceiling(p.Cy + ey));
        var w = x1 - x0; var h = y1 - y0;
        if (w < 4 || h < 4) return Fallback;

        var crop = new CroppedBitmap(img, new Int32Rect(x0, y0, w, h));
        var conv = new FormatConvertedBitmap(crop, PixelFormats.Bgra32, null, 0);
        var stride = w * 4;
        var px = new byte[stride * h];
        conv.CopyPixels(px, stride, 0);

        var sclera = new List<(int L, int R, int G, int B)>();
        var iris = new List<(int L, int R, int G, int B)>();
        var skin = new List<(int L, int R, int G, int B)>();
        double a = p.Width / 2, b = p.Height / 2;

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * stride + x * 4;
            int B = px[i], G = px[i + 1], R = px[i + 2];
            var l = (R * 299 + G * 587 + B * 114) / 1000;
            var (u, v) = GazeMath.Rotate(x0 + x + 0.5 - p.Cx, y0 + y + 0.5 - p.Cy, -p.TiltDeg);
            var e = (u * u) / (a * a) + (v * v) / (b * b);
            var d = Math.Sqrt(u * u + v * v);
            if (e <= 1)
            {
                if (d > p.IrisR * 1.15) sclera.Add((l, R, G, B));
                else if (d > p.IrisR * 0.45 && d < p.IrisR * 0.92 && l is > 18 and < 235) iris.Add((l, R, G, B));
            }
            else if (e is > 1.4 and < 4.0 && l > 25) skin.Add((l, R, G, B));
        }

        var sc = sclera.Count >= 8 ? Mean(sclera.OrderByDescending(t => t.L).Take(Math.Max(4, sclera.Count * 3 / 10))) : Fallback.Sclera;
        // Whites of eyes are almost neutral: pull the sample toward grey so a warm light does not tint them.
        sc = Mix(sc, Color.FromRgb((byte)((sc.R + sc.G + sc.B) / 3), (byte)((sc.R + sc.G + sc.B) / 3), (byte)((sc.R + sc.G + sc.B) / 3)), 0.5);
        var ic = iris.Count >= 6 ? Mean(iris) : Fallback.Iris;
        var sk = skin.Count >= 12
            ? Mean(skin.OrderBy(t => t.L).Skip(skin.Count / 4).Take(skin.Count / 2))
            : Fallback.Skin;
        return new EyeColors(sc, ic, sk);
    }

    static Color Mean(IEnumerable<(int L, int R, int G, int B)> s)
    {
        long r = 0, g = 0, b = 0; var n = 0;
        foreach (var t in s) { r += t.R; g += t.G; b += t.B; n++; }
        return n == 0 ? Colors.Gray : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
