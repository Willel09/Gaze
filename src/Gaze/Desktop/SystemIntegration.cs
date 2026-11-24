using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using Gaze.Core;
using Microsoft.Win32;

namespace Gaze.Desktop;

public static class SystemIntegration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "Gaze";

    public static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (k == null) return;
        if (on) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --background");
        else k.DeleteValue(RunName, false);
    }

    public static string? CurrentWallpaper()
    {
        var sb = new StringBuilder(520);
        return Native.SystemParametersInfo(Native.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) && sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>
    /// Sets the same picture as the real Windows wallpaper. That is what you see while eye tracking is off,
    /// on the lock screen preview and if the overlay ever fails to attach.
    /// </summary>
    public static void SetSystemWallpaper(string imagePath)
    {
        try
        {
            var ext = Path.GetExtension(imagePath).ToLowerInvariant();
            var file = imagePath;
            if (ext is not (".jpg" or ".jpeg" or ".png" or ".bmp"))
            {
                file = Path.Combine(SettingsStore.Dir, "system-wallpaper.png");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(ImageLoader.Load(imagePath)));
                using var fs = File.Create(file);
                enc.Save(fs);
            }
            using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true))
            {
                k?.SetValue("WallpaperStyle", "10"); // Fill
                k?.SetValue("TileWallpaper", "0");
            }
            Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, file, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    public static void RestoreWallpaper(string? path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, path, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE);
    }
}
