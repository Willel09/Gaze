using System;

namespace Gaze.Core;

public static class VersionCompare
{
    /// <summary>True when a release tag such as "v1.2.0" is newer than the running version.</summary>
    public static bool IsNewer(string? tag, Version current)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var t = tag.Trim().TrimStart('v', 'V');
        var dash = t.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) t = t[..dash];
        return Version.TryParse(t, out var v) && Normalize(v) > Normalize(current);
    }

    static Version Normalize(Version v) => new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));
}
