using Gaze.Core;
using Xunit;

namespace Gaze.Tests;

public class GazeMathTests
{
    [Fact]
    public void CursorOnTheEye_GivesNoOffset()
    {
        Assert.Equal((0.0, 0.0), GazeMath.Target(0, 0, 20, 8, 300));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, -1)]
    [InlineData(3, 4)]
    [InlineData(-900, 700)]
    [InlineData(1e6, 1e6)]
    public void Pupil_NeverLeavesTheEyeEllipse(double dx, double dy)
    {
        const double rx = 20, ry = 8;
        var (x, y) = GazeMath.Target(dx, dy, rx, ry, 300);
        Assert.True(x * x / (rx * rx) + y * y / (ry * ry) <= 1.0 + 1e-9);
    }

    [Fact]
    public void FartherCursor_MovesPupilFurther_UpToTheLimit()
    {
        var near = GazeMath.Target(50, 0, 20, 8, 300).X;
        var far = GazeMath.Target(2000, 0, 20, 8, 300).X;
        Assert.InRange(near, 0.1, far);
        Assert.True(far <= 20 && far > 19.9);
    }

    [Fact]
    public void HigherSensitivity_ShortensTheReach()
    {
        // Sensitivity divides the reach distance, so the same cursor distance gives a bigger move.
        var lazy = GazeMath.Target(100, 0, 20, 8, 300 / 0.5).X;
        var keen = GazeMath.Target(100, 0, 20, 8, 300 / 2.0).X;
        Assert.True(keen > lazy);
    }

    [Fact]
    public void SmoothDamp_ConvergesWithoutOvershoot()
    {
        double pos = 0, vel = 0;
        var max = 0.0;
        for (var i = 0; i < 240; i++)
        {
            GazeMath.SmoothDamp(ref pos, ref vel, 10, GazeMath.SmoothTime(0.35), 1.0 / 60);
            max = Math.Max(max, pos);
        }
        Assert.InRange(pos, 9.99, 10.0);
        Assert.True(max <= 10.0 + 1e-6);
    }

    [Fact]
    public void SmoothDamp_IsDelayed_NotInstant()
    {
        double pos = 0, vel = 0;
        GazeMath.SmoothDamp(ref pos, ref vel, 10, GazeMath.SmoothTime(0.35), 1.0 / 60);
        Assert.InRange(pos, 0.01, 3);
    }

    [Fact]
    public void Layout_UsesEyeDistanceAndHeadTilt()
    {
        var s = new EyeSettings { LeftX = 0.4, LeftY = 0.5, RightX = 0.6, RightY = 0.5, LeftSet = true, RightSet = true };
        var (l, r) = EyeLayout.Compute(s, 1000, 1000);
        Assert.Equal(200, r.Cx - l.Cx, 6);
        Assert.Equal(0, l.TiltDeg, 6);
        Assert.Equal(200 * s.EyeWidth, l.Width, 6);

        s.RightY = 0.6; // head tilted: eye line rotates
        var (_, r2) = EyeLayout.Compute(s, 1000, 1000);
        Assert.InRange(r2.TiltDeg, 26, 28);
    }

    [Fact]
    public void Layout_IgnoresWhichEyeWasMarkedFirst()
    {
        var a = new EyeSettings { LeftX = 0.4, LeftY = 0.5, RightX = 0.6, RightY = 0.5 };
        var b = new EyeSettings { LeftX = 0.6, LeftY = 0.5, RightX = 0.4, RightY = 0.5 };
        Assert.Equal(EyeLayout.Compute(a, 800, 600), EyeLayout.Compute(b, 800, 600));
    }

    [Fact]
    public void Settings_RoundTripThroughJson()
    {
        var s = new AppSettings { WallpaperFile = "wallpaper.png", Applied = true };
        s.Eyes.LeftSet = true; s.Eyes.LeftX = 0.123;
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(s))!;
        Assert.Equal(0.123, back.Eyes.LeftX);
        Assert.True(back.Applied);
    }

    [Fact]
    public void Cyclops_GivesOneEyeSizedByItsOwnSetting()
    {
        var s = new EyeSettings { Cyclops = true, LeftSet = true, LeftX = 0.5, LeftY = 0.4, CyclopsSize = 0.2 };
        var poses = EyeLayout.Poses(s, 1000, 800);
        Assert.Single(poses);
        Assert.Equal(500, poses[0].Cx, 6);
        Assert.Equal(0.2 * 1000 * s.EyeWidth, poses[0].Width, 6);
        Assert.True(s.IsCalibrated);
        s.Cyclops = false;
        Assert.False(s.IsCalibrated);
        Assert.Equal(2, EyeLayout.Poses(s, 1000, 800).Length);
    }

    [Theory]
    [InlineData("v1.2.0", true)]
    [InlineData("v1.1.1", true)]
    [InlineData("1.1.0", false)]
    [InlineData("v1.0.9", false)]
    [InlineData("v2.0.0-beta", true)]
    [InlineData("nonsense", false)]
    [InlineData(null, false)]
    public void Update_Detection(string? tag, bool newer)
    {
        Assert.Equal(newer, VersionCompare.IsNewer(tag, new Version(1, 1, 0, 0)));
    }

    [Fact]
    public void EyeSettings_Clone_IsIndependent()
    {
        var a = new EyeSettings { LeftX = 0.3, IrisColor = "#112233", Pupil = PupilShape.Slit };
        var b = a.Clone();
        b.LeftX = 0.9; b.IrisColor = null;
        Assert.Equal(0.3, a.LeftX);
        Assert.Equal("#112233", a.IrisColor);
        Assert.Equal(PupilShape.Slit, b.Pupil);
    }

    [Fact]
    public void OppositeCursorGivesOppositePupil()
    {
        var a = GazeMath.Target(120, -40, 20, 8, 300);
        var b = GazeMath.Target(-120, 40, 20, 8, 300);
        Assert.Equal(-a.X, b.X, 9);
        Assert.Equal(-a.Y, b.Y, 9);
    }
}
