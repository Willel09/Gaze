using System;
using System.IO;
using System.Windows.Threading;
using Gaze.Core;
using Gaze.Desktop;
using Microsoft.Win32;

namespace Gaze;

public enum TrackingStatus { NotApplied, Off, Watching, Paused, Error }

/// <summary>Owns settings and the desktop overlay; the window and tray icon are thin views over this.</summary>
public sealed class AppController : IDisposable
{
    readonly DispatcherTimer _rebuild = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public AppSettings Settings { get; private set; } = SettingsStore.Load();
    public WallpaperHost Host { get; } = new();
    public string? LastError { get; private set; }
    public event Action? StatusChanged;

    public AppController()
    {
        Host.ActiveChanged += () => { if (Host.Error != null) LastError = Host.Error; StatusChanged?.Invoke(); };
        Host.Verified += name => { Settings.WorkingMethod = name; Save(); };
        _rebuild.Tick += (_, _) => { _rebuild.Stop(); Restart(); };
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.SessionSwitch += OnSession;
    }

    public string? WallpaperPath =>
        Settings.WallpaperFile is { } f && File.Exists(Path.Combine(SettingsStore.Dir, f)) ? Path.Combine(SettingsStore.Dir, f) : null;

    public TrackingStatus Status =>
        LastError != null ? TrackingStatus.Error
        : !Settings.Applied ? TrackingStatus.NotApplied
        : !Settings.TrackingEnabled ? TrackingStatus.Off
        : Host.AnyActive ? TrackingStatus.Watching
        : TrackingStatus.Paused;

    /// <summary>Copies the picture into the app folder so it keeps working if the original moves.</summary>
    public string ImportWallpaper(string source)
    {
        // Decode once up front so a bad file fails here, not on the desktop.
        ImageLoader.Probe(source);
        Directory.CreateDirectory(SettingsStore.Dir);
        var name = "wallpaper" + Path.GetExtension(source).ToLowerInvariant();
        foreach (var old in Directory.GetFiles(SettingsStore.Dir, "wallpaper.*")) File.Delete(old);
        File.Copy(source, Path.Combine(SettingsStore.Dir, name), overwrite: true);
        Settings.WallpaperFile = name;
        Settings.WallpaperName = Path.GetFileName(source);
        Settings.Eyes.LeftSet = Settings.Eyes.RightSet = false;
        Save();
        return Path.Combine(SettingsStore.Dir, name);
    }

    public async System.Threading.Tasks.Task<bool> ApplyAsync()
    {
        var path = WallpaperPath;
        if (path == null || !Settings.Eyes.IsCalibrated) return false;

        if (Settings.PreviousSystemWallpaper == null)
        {
            var current = SystemIntegration.CurrentWallpaper();
            if (!string.IsNullOrEmpty(current) && !current.StartsWith(SettingsStore.Dir, StringComparison.OrdinalIgnoreCase))
                Settings.PreviousSystemWallpaper = current;
        }
        SystemIntegration.SetSystemWallpaper(path);

        Settings.Applied = true;
        Settings.TrackingEnabled = true;
        Save();
        // Explorer repaints the wallpaper a moment after it changes; attach after that so it cannot paint over the overlay.
        await System.Threading.Tasks.Task.Delay(1200);
        return Restart();
    }

    public void SetTracking(bool on)
    {
        Settings.TrackingEnabled = on;
        Save();
        if (on) Restart(); else { Host.Stop(); LastError = null; StatusChanged?.Invoke(); }
    }

    /// <summary>Called at launch and after display / power changes.</summary>
    public bool Restart()
    {
        LastError = null;
        var path = WallpaperPath;
        if (!Settings.Applied || !Settings.TrackingEnabled || path == null) { Host.Stop(); StatusChanged?.Invoke(); return true; }
        var ok = Host.Start(path, Settings.Eyes, Settings.WorkingMethod);
        if (!ok) LastError = Host.Error ?? "Could not attach to the desktop.";
        StatusChanged?.Invoke();
        return ok;
    }

    public void SetAutostart(bool on)
    {
        Settings.StartWithWindows = on;
        SystemIntegration.SetAutostart(on);
        Save();
    }

    public void Reset()
    {
        Host.Stop();
        SystemIntegration.SetAutostart(false);
        SystemIntegration.RestoreWallpaper(Settings.PreviousSystemWallpaper);
        try { foreach (var f in Directory.GetFiles(SettingsStore.Dir)) File.Delete(f); } catch { /* best effort */ }
        Settings = new AppSettings();
        LastError = null;
        Save();
        StatusChanged?.Invoke();
    }

    public void Save() => SettingsStore.Save(Settings);

    void OnDisplayChanged(object? s, EventArgs e) => Dispatch(() => { _rebuild.Stop(); _rebuild.Start(); });
    void OnPower(object? s, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) Dispatch(() => { _rebuild.Stop(); _rebuild.Start(); }); }
    void OnSession(object? s, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            Dispatch(() => Host.SetLocked(true));
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            Dispatch(() => Host.SetLocked(false));
    }

    static void Dispatch(Action a) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(a);

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.SessionSwitch -= OnSession;
        Host.Dispose();
    }
}
