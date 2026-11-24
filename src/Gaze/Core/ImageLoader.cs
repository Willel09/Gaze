using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Gaze.Core;

public static class ImageLoader
{
    public static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

    public static (int W, int H) Probe(string path)
    {
        var (bytes, orient) = ReadBytes(path);
        using var ms = new MemoryStream(bytes);
        var f = BitmapFrame.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        return orient is >= 5 and <= 8 ? (f.PixelHeight, f.PixelWidth) : (f.PixelWidth, f.PixelHeight);
    }

    /// <summary>Decodes the image upright and frozen. targetWidth &gt; 0 decodes at that width (never upscales).</summary>
    public static BitmapSource Load(string path, int targetWidth = 0)
    {
        var (bytes, orient) = ReadBytes(path);
        using var ms = new MemoryStream(bytes);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.StreamSource = ms;
        bi.CacheOption = BitmapCacheOption.OnLoad;
        if (targetWidth > 0)
        {
            var probe = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var rotated = orient is >= 5 and <= 8;
            var natural = rotated ? probe.PixelHeight : probe.PixelWidth;
            if (targetWidth < natural)
            {
                if (rotated) bi.DecodePixelHeight = targetWidth; else bi.DecodePixelWidth = targetWidth;
            }
        }
        bi.EndInit();
        bi.Freeze();
        return Orient(bi, orient);
    }

    /// <summary>Raw bytes plus EXIF orientation. WebP falls back to SkiaSharp when Windows has no WebP codec.</summary>
    static (byte[] Bytes, int Orientation) ReadBytes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var orient = ReadOrientation(bytes);
        if (!CanDecode(bytes))
        {
            bytes = TranscodeToPng(bytes);
            orient = 1;
        }
        return (bytes, orient);
    }

    static bool CanDecode(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            _ = BitmapFrame.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelWidth;
            return true;
        }
        catch { return false; }
    }

    static byte[] TranscodeToPng(byte[] bytes)
    {
        using var bmp = SkiaSharp.SKBitmap.Decode(bytes)
            ?? throw new InvalidDataException("This image format is not supported.");
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static int ReadOrientation(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var f = BitmapFrame.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (f.Metadata is BitmapMetadata m && m.ContainsQuery("System.Photo.Orientation")
                && m.GetQuery("System.Photo.Orientation") is ushort o) return o;
        }
        catch { /* no metadata */ }
        return 1;
    }

    static BitmapSource Orient(BitmapSource src, int o)
    {
        BitmapSource Rot(BitmapSource s, double deg) => Frozen(new TransformedBitmap(s, new RotateTransform(deg)));
        BitmapSource Flip(BitmapSource s, double x, double y) => Frozen(new TransformedBitmap(s, new ScaleTransform(x, y)));
        return o switch
        {
            2 => Flip(src, -1, 1),
            3 => Rot(src, 180),
            4 => Flip(src, 1, -1),
            5 => Flip(Rot(src, 90), -1, 1),
            6 => Rot(src, 90),
            7 => Flip(Rot(src, 270), -1, 1),
            8 => Rot(src, 270),
            _ => src,
        };
    }

    static BitmapSource Frozen(BitmapSource b) { b.Freeze(); return b; }
}
