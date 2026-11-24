using System;
using System.IO;

namespace Gaze.Core;

/// <summary>Tiny file log so "the eyes do not show" can be diagnosed on a machine I cannot see.</summary>
public static class Log
{
    public static string Path { get; } = System.IO.Path.Combine(SettingsStore.Dir, "gaze.log");
    static readonly object Gate = new();
    static bool _started;

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.Dir);
                if (!_started)
                {
                    _started = true;
                    if (File.Exists(Path) && new FileInfo(Path).Length > 200_000) File.Delete(Path);
                }
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
