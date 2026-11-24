using System;
using System.IO;
using System.Text.Json;

namespace Gaze.Core;

public enum PupilShape { Round, Slit, Horizontal }

/// <summary>Eye calibration. Centers are 0..1 of the image; sizes are multiples of the distance between the eyes.</summary>
public sealed class EyeSettings
{
    public bool LeftSet { get; set; }
    public bool RightSet { get; set; }
    public double LeftX { get; set; } = 0.42;
    public double LeftY { get; set; } = 0.40;
    public double RightX { get; set; } = 0.58;
    public double RightY { get; set; } = 0.40;

    public double EyeWidth { get; set; } = 0.50;
    public double EyeHeight { get; set; } = 0.22;
    public double IrisSize { get; set; } = 0.10;
    public double PupilSize { get; set; } = 0.42;
    public double Tilt { get; set; } = 0;
    public double LidShadow { get; set; } = 0.35;

    public double Range { get; set; } = 0.85;
    public double Sensitivity { get; set; } = 1.0;
    public double Smoothness { get; set; } = 0.35;
    public bool Blink { get; set; } = false;

    /// <summary>One big eye instead of two. Its size is CyclopsSize (fraction of image width) in place of the eye distance.</summary>
    public bool Cyclops { get; set; }
    public double CyclopsSize { get; set; } = 0.20;

    /// <summary>Appearance. Null colour = sampled from the photo. Hex like #5B8DEF.</summary>
    public string? IrisColor { get; set; }
    public string? ScleraColor { get; set; }
    public PupilShape Pupil { get; set; } = PupilShape.Round;
    public bool Glint { get; set; } = true;
    /// <summary>Permanent lowered upper lid, 0 = wide awake, 0.5 = sleepy.</summary>
    public double Droop { get; set; }

    /// <summary>Eyes glance around when the mouse is idle and fall asleep after a few minutes.</summary>
    public bool IdleLife { get; set; } = true;
    /// <summary>Quick blink whenever you click.</summary>
    public bool ClickBlink { get; set; } = true;

    public bool IsCalibrated => LeftSet && (Cyclops || RightSet);

    public void ResetShape()
    {
        var d = new EyeSettings();
        EyeWidth = d.EyeWidth; EyeHeight = d.EyeHeight; IrisSize = d.IrisSize;
        PupilSize = d.PupilSize; Tilt = d.Tilt; LidShadow = d.LidShadow; Droop = d.Droop;
    }
}

public sealed class AppSettings
{
    public string? WallpaperFile { get; set; }
    public string? WallpaperName { get; set; }
    public string? PreviousSystemWallpaper { get; set; }
    public EyeSettings Eyes { get; set; } = new();
    public bool Applied { get; set; }
    public bool TrackingEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; }

    /// <summary>The desktop attach method that was proven to work on this PC.</summary>
    public string? WorkingMethod { get; set; }

}

public static class SettingsStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Gaze");

    static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt file: start fresh rather than crash */ }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* disk full / locked: settings are best effort */ }
    }
}
