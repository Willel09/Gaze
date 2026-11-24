using System;
using System.Windows.Media;

namespace Gaze.Core;

/// <summary>
/// One shared vsync-driven tick, capped near 60 fps. It only hooks WPF's render loop while someone is subscribed,
/// so a paused desktop costs nothing.
/// </summary>
public static class FrameClock
{
    static Action<double>[] _subs = Array.Empty<Action<double>>();
    static TimeSpan _last;
    static bool _hooked;

    public static void Add(Action<double> cb)
    {
        if (Array.IndexOf(_subs, cb) >= 0) return;
        var n = new Action<double>[_subs.Length + 1];
        _subs.CopyTo(n, 0); n[^1] = cb; _subs = n;
        if (!_hooked) { _last = TimeSpan.Zero; CompositionTarget.Rendering += OnRender; _hooked = true; }
    }

    public static void Remove(Action<double> cb)
    {
        var list = new System.Collections.Generic.List<Action<double>>(_subs);
        if (!list.Remove(cb)) return;
        _subs = list.ToArray();
        if (_subs.Length == 0 && _hooked) { CompositionTarget.Rendering -= OnRender; _hooked = false; }
    }

    static void OnRender(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        var first = _last == TimeSpan.Zero;
        var dt = first ? 1.0 / 60 : (t - _last).TotalSeconds;
        if (!first && dt < 0.0145) return;
        _last = t;
        dt = Math.Min(dt, 0.1);
        foreach (var s in _subs) s(dt);
    }
}
