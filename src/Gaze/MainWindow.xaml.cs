using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Threading.Tasks;
using System.Windows.Threading;
using Gaze.Core;
using Gaze.Desktop;
using Gaze.Rendering;
using Microsoft.Win32;

namespace Gaze;

public partial class MainWindow : Window
{
    readonly AppController _app;
    readonly WallpaperScene _scene = new();
    readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(400) };
    System.Windows.Media.Imaging.BitmapSource? _bmp;
    bool _loaded;
    int _drag = -1;

    EyeSettings E => _app.Settings.Eyes;
    bool Calibrating => ModeCalibrate.IsChecked == true;
    int Selected => !E.Cyclops && PickRight.IsChecked == true ? 1 : 0;
    bool _building;

    public MainWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        SceneHost.Children.Add(_scene);
        StageGrid.Focusable = true;
        StageGrid.KeyDown += OnStageKey;

        _save.Tick += (_, _) => { _save.Stop(); _app.Save(); };
        _app.StatusChanged += UpdateStatus;

        Bind(SlWidth, E.EyeWidth, v => E.EyeWidth = v);
        Bind(SlHeight, E.EyeHeight, v => E.EyeHeight = v);
        Bind(SlIris, E.IrisSize, v => E.IrisSize = v);
        Bind(SlPupil, E.PupilSize, v => E.PupilSize = v);
        Bind(SlTilt, E.Tilt, v => E.Tilt = v);
        Bind(SlLid, E.LidShadow, v => E.LidShadow = v);
        Bind(SlSens, E.Sensitivity, v => E.Sensitivity = v);
        Bind(SlRange, E.Range, v => E.Range = v);
        Bind(SlSmooth, E.Smoothness, v => E.Smoothness = v);
        Bind(SlCyclops, E.CyclopsSize, v => E.CyclopsSize = v);
        BuildSwatches();
        SyncAppearance();
        BlinkSwitch.IsChecked = E.Blink;
        TrackSwitch.IsChecked = _app.Settings.Applied && _app.Settings.TrackingEnabled;
        StartSwitch.IsChecked = _app.Settings.StartWithWindows;

        LoadImage();
        UpdateStatus();
        UpdateMode();
        ApplyCyclopsUi();

        SourceInitialized += (_, _) => DarkTitleBar();
        Loaded += (_, _) => { _loaded = true; Intro(); LayoutStage(); };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) FrameClock.Add(OnFrame); else FrameClock.Remove(OnFrame);
        };
        Closing += OnClosing;
        VersionText.Text = "Version " + Updater.Current.ToString(3);
        Loaded += async (_, _) => { await Task.Delay(4000); await CheckUpdate(silent: true); };
    }

    // ---- window chrome ----------------------------------------------------------------------------------------

    void DarkTitleBar()
    {
        var h = new WindowInteropHelper(this).Handle;
        var on = 1;
        Native.DwmSetWindowAttribute(h, 20, ref on, sizeof(int)); // Windows 10 20H1+ and 11
        Native.DwmSetWindowAttribute(h, 19, ref on, sizeof(int)); // older Windows 10 builds
    }

    void Intro()
    {
        Root.Opacity = 0;
        var t = new TranslateTransform(0, 8);
        Root.RenderTransform = t;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        // Keep the eyes alive in the tray; quit only if nothing is applied.
        if (_app.Settings.Applied && _app.Settings.TrackingEnabled && Application.Current is App { } app)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _app.Save();
        (Application.Current as App)?.Quit();
    }

    // ---- image + stage ----------------------------------------------------------------------------------------

    void Bind(LabeledSlider s, double v, Action<double> set)
    {
        s.Value = v;
        s.Changed += x => { set(x); OnEyesChanged(); };
    }

    void LoadImage()
    {
        _bmp = null;
        var path = _app.WallpaperPath;
        if (path != null)
        {
            try { _bmp = ImageLoader.Load(path, 3200); }
            catch (Exception ex) { Toast("Could not open the saved wallpaper: " + ex.Message); }
        }
        FileName.Text = _bmp != null ? (_app.Settings.WallpaperName ?? "Wallpaper") : "JPG, PNG, WebP or BMP";
        EmptyState.Visibility = _bmp == null ? Visibility.Visible : Visibility.Collapsed;
        StageFrame.Visibility = _bmp == null ? Visibility.Collapsed : Visibility.Visible;
        RefreshMarks();
        LayoutStage();
    }

    void OnStageSize(object sender, SizeChangedEventArgs e) => LayoutStage();

    void LayoutStage()
    {
        if (_bmp == null || !_loaded && !IsLoaded) return;
        double aw = StageHost.ActualWidth - 2, ah = StageHost.ActualHeight - 2;
        if (aw < 40 || ah < 40) return;
        var k = Math.Min(aw / _bmp.PixelWidth, ah / _bmp.PixelHeight);
        double w = Math.Floor(_bmp.PixelWidth * k), h = Math.Floor(_bmp.PixelHeight * k);
        StageFrame.Width = w + 2; StageFrame.Height = h + 2;
        Rebuild();
    }

    void Rebuild()
    {
        if (_bmp == null || StageFrame.Width < 4) return;
        _scene.Configure(_bmp, E, StageFrame.Width - 2, StageFrame.Height - 2);
        _scene.SetEyeOpacity(Calibrating ? 0.6 : 1.0);
        DrawGuides();
    }

    void OnEyesChanged()
    {
        Rebuild();
        _save.Stop(); _save.Start();
    }

    void OnFrame(double dt)
    {
        if (WindowState == WindowState.Minimized || !_scene.Ready || !StageFrame.IsVisible) return;
        if (!Native.GetCursorPos(out var c)) return;
        _scene.Update(StageGrid.PointFromScreen(new Point(c.X, c.Y)), dt);
    }

    // ---- calibration guides -----------------------------------------------------------------------------------

    void DrawGuides()
    {
        Guides.Children.Clear();
        if (_bmp == null) return;
        double W = _scene.Width, H = _scene.Height;
        var accent = (Brush)FindResource("AccentText");

        if (E.IsCalibrated)
        {
            var poses = _scene.Poses;
            for (var i = 0; i < poses.Length; i++) AddEyeGuide(poses[i], accent, Selected == i);
        }
        else
        {
            if (E.LeftSet) AddCross(E.LeftX * W, E.LeftY * H, accent, Selected == 0);
            if (E.RightSet && !E.Cyclops) AddCross(E.RightX * W, E.RightY * H, accent, Selected == 1);
        }
    }

    void AddEyeGuide(EyePose p, Brush brush, bool selected)
    {
        var g = new Canvas { RenderTransform = new RotateTransform(p.TiltDeg) };
        Canvas.SetLeft(g, p.Cx); Canvas.SetTop(g, p.Cy);
        var stroke = selected ? Brushes.White : brush;
        var eye = new Ellipse { Width = p.Width, Height = p.Height, Stroke = stroke, StrokeThickness = selected ? 1.75 : 1.25 };
        Canvas.SetLeft(eye, -p.Width / 2); Canvas.SetTop(eye, -p.Height / 2);
        var iris = new Ellipse { Width = p.IrisR * 2, Height = p.IrisR * 2, Stroke = stroke, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }, Opacity = 0.85 };
        Canvas.SetLeft(iris, -p.IrisR); Canvas.SetTop(iris, -p.IrisR);
        var dot = new Ellipse { Width = 5, Height = 5, Fill = stroke };
        Canvas.SetLeft(dot, -2.5); Canvas.SetTop(dot, -2.5);
        g.Children.Add(eye); g.Children.Add(iris); g.Children.Add(dot);
        Guides.Children.Add(g);
    }

    void AddCross(double x, double y, Brush brush, bool selected)
    {
        var b = selected ? Brushes.White : brush;
        foreach (var (x1, y1, x2, y2) in new[] { (-10.0, 0.0, 10.0, 0.0), (0.0, -10.0, 0.0, 10.0) })
            Guides.Children.Add(new Line { X1 = x + x1, Y1 = y + y1, X2 = x + x2, Y2 = y + y2, Stroke = b, StrokeThickness = 1.75 });
    }

    // ---- stage interaction ------------------------------------------------------------------------------------

    void OnStageDown(object sender, MouseButtonEventArgs e)
    {
        if (_bmp == null || !Calibrating) return;
        StageGrid.Focus();
        var p = e.GetPosition(StageGrid);
        double W = _scene.Width, H = _scene.Height;

        _drag = -1;
        var best = 28.0;
        if (E.LeftSet) { var d = Dist(p, E.LeftX * W, E.LeftY * H); if (d < best) { best = d; _drag = 0; } }
        if (E.RightSet && !E.Cyclops) { var d = Dist(p, E.RightX * W, E.RightY * H); if (d < best) { best = d; _drag = 1; } }

        if (_drag < 0)
        {
            _drag = Selected;
            SetEye(_drag, p);
            // After the first eye, jump to the other one so two clicks finish the job.
            if (E.Cyclops) { }
            else if (_drag == 0 && !E.RightSet) PickRight.IsChecked = true;
            else if (_drag == 1 && !E.LeftSet) PickLeft.IsChecked = true;
        }
        else
        {
            if (!E.Cyclops) (_drag == 0 ? PickLeft : PickRight).IsChecked = true;
        }
        StageGrid.CaptureMouse();
        e.Handled = true;
    }

    void OnStageMove(object sender, MouseEventArgs e)
    {
        if (_drag < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        SetEye(_drag, e.GetPosition(StageGrid));
    }

    void OnStageUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag < 0) return;
        _drag = -1;
        StageGrid.ReleaseMouseCapture();
        RefreshMarks();
    }

    void OnStageKey(object sender, KeyEventArgs e)
    {
        if (!Calibrating || _bmp == null) return;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 3.0 : 0.5;
        double dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
        double dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
        if (dx == 0 && dy == 0) return;
        var i = Selected;
        var set = i == 0 ? E.LeftSet : E.RightSet;
        if (!set) return;
        double W = _scene.Width, H = _scene.Height;
        var cx = (i == 0 ? E.LeftX : E.RightX) * W + dx;
        var cy = (i == 0 ? E.LeftY : E.RightY) * H + dy;
        SetEye(i, new Point(cx, cy));
        e.Handled = true;
    }

    static double Dist(Point p, double x, double y) => Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));

    void SetEye(int i, Point p)
    {
        double nx = Math.Clamp(p.X / _scene.Width, 0, 1), ny = Math.Clamp(p.Y / _scene.Height, 0, 1);
        if (i == 0) { E.LeftX = nx; E.LeftY = ny; E.LeftSet = true; }
        else { E.RightX = nx; E.RightY = ny; E.RightSet = true; }
        RefreshMarks();
        OnEyesChanged();
    }

    void RefreshMarks()
    {
        LeftMark.Visibility = E.LeftSet ? Visibility.Visible : Visibility.Collapsed;
        RightMark.Visibility = E.RightSet ? Visibility.Visible : Visibility.Collapsed;
        EyeHint.Text = E.Cyclops
            ? (E.LeftSet ? "The eye is marked. Set its size, fine-tune the shape, then switch to Preview."
                         : "Click the centre of the eye on the photo. Drag the marker to adjust it, or use the arrow keys.")
            : !E.LeftSet || !E.RightSet
                ? $"Click the centre of the {(Selected == 0 ? "left" : "right")} eye on the photo. Drag a marker to adjust it, or use the arrow keys."
                : "Both eyes are marked. Fine-tune the shape, then switch to Preview.";
        UpdateMode();
    }

    void OnPickEye(object sender, RoutedEventArgs e) { if (IsLoaded) { RefreshMarks(); DrawGuides(); } }

    // ---- modes ------------------------------------------------------------------------------------------------

    void OnMode(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateMode();
        _scene.SetEyeOpacity(Calibrating ? 0.6 : 1.0);
        var to = Calibrating ? 1.0 : 0.0;
        Guides.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)));
    }

    void UpdateMode()
    {
        if (StageHint == null) return;
        StageHint.Text = Calibrating
            ? "Click to place the selected eye, drag markers to adjust"
            : E.IsCalibrated ? "Move your cursor. The eyes follow it" : "Mark both eyes in Calibrate first";
    }

    // ---- sidebar actions --------------------------------------------------------------------------------------

    void OnChoose(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose wallpaper",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _app.ImportWallpaper(dlg.FileName);
            PickLeft.IsChecked = true;
            ModeCalibrate.IsChecked = true;
            LoadImage();
            Toast(E.Cyclops ? "Now click the eye" : "Now mark the left and right eye");
        }
        catch (Exception ex)
        {
            Toast("Could not open that image: " + ex.Message);
        }
    }

    // ---- cyclops + appearance ---------------------------------------------------------------------------------

    static readonly (string? Hex, string Name)[] IrisPalette =
    {
        (null, "Auto"), ("#6B4A2F", "Brown"), ("#8A6A2B", "Hazel"), ("#4F7A3A", "Green"), ("#3E7BD6", "Blue"),
        ("#7E8A94", "Grey"), ("#C98A1E", "Amber"), ("#B3262E", "Red"), ("#7B4FD1", "Violet"), ("#18B8C9", "Cyan"),
    };
    static readonly (string? Hex, string Name)[] ScleraPalette =
    {
        (null, "Auto"), ("#F2F2F4", "White"), ("#EDE3D0", "Warm"), ("#E9B8B0", "Bloodshot"),
        ("#E8D77A", "Yellow"), ("#9FD6FF", "Ice"), ("#15151A", "Black"),
    };

    void BuildSwatches()
    {
        Fill(IrisSwatches, "iris", IrisPalette, h => E.IrisColor = h);
        Fill(ScleraSwatches, "sclera", ScleraPalette, h => E.ScleraColor = h);
    }

    void Fill(Panel host, string group, (string? Hex, string Name)[] palette, Action<string?> set)
    {
        foreach (var (hex, name) in palette)
        {
            var rb = new RadioButton
            {
                GroupName = group, Style = (Style)FindResource("Swatch"), ToolTip = name, Tag = hex,
                Background = hex == null ? (Brush)FindResource("FillSelected") : new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                Content = hex == null ? "A" : null,
            };
            rb.Checked += (_, _) => { if (_building) return; set(hex); OnEyesChanged(); };
            host.Children.Add(rb);
        }
    }

    void SyncAppearance()
    {
        _building = true;
        foreach (var (panel, current) in new[] { (IrisSwatches, E.IrisColor), (ScleraSwatches, E.ScleraColor) })
            foreach (RadioButton rb in panel.Children)
                rb.IsChecked = string.Equals(rb.Tag as string, current, StringComparison.OrdinalIgnoreCase);
        (E.Pupil switch { PupilShape.Slit => PupilSlit, PupilShape.Horizontal => PupilWide, _ => PupilRound }).IsChecked = true;
        GlintSwitch.IsChecked = E.Glint;
        CyclopsSwitch.IsChecked = E.Cyclops;
        SlCyclops.Value = E.CyclopsSize;
        _building = false;
    }

    void OnPupil(object sender, RoutedEventArgs e)
    {
        if (_building || !IsLoaded) return;
        E.Pupil = PupilSlit.IsChecked == true ? PupilShape.Slit : PupilWide.IsChecked == true ? PupilShape.Horizontal : PupilShape.Round;
        OnEyesChanged();
    }

    void OnGlint(object sender, RoutedEventArgs e) { E.Glint = GlintSwitch.IsChecked == true; OnEyesChanged(); }

    void OnCyclops(object sender, RoutedEventArgs e)
    {
        E.Cyclops = CyclopsSwitch.IsChecked == true;
        if (E.Cyclops) PickLeft.IsChecked = true;
        ApplyCyclopsUi();
        RefreshMarks();
        OnEyesChanged();
    }

    void ApplyCyclopsUi()
    {
        EyePicker.Visibility = E.Cyclops ? Visibility.Collapsed : Visibility.Visible;
        SlCyclops.Visibility = E.Cyclops ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnAutoFit(object sender, RoutedEventArgs e)
    {
        E.ResetShape();
        SlWidth.Value = E.EyeWidth; SlHeight.Value = E.EyeHeight; SlIris.Value = E.IrisSize;
        SlPupil.Value = E.PupilSize; SlTilt.Value = E.Tilt; SlLid.Value = E.LidShadow;
        OnEyesChanged();
    }

    void OnBlink(object sender, RoutedEventArgs e) { E.Blink = BlinkSwitch.IsChecked == true; OnEyesChanged(); }

    void OnTrackingSwitch(object sender, RoutedEventArgs e)
    {
        var on = TrackSwitch.IsChecked == true;
        if (on && !_app.Settings.Applied) { TrackSwitch.IsChecked = false; Toast("Apply to desktop first"); return; }
        _app.SetTracking(on);
    }

    UpdateInfo? _update;
    bool _busy;

    async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_update == null) { await CheckUpdate(silent: false); return; }

        _busy = true;
        UpdateBtn.IsEnabled = false;
        try
        {
            var p = new Progress<double>(v => UpdateLabel.Text = $"Downloading {(int)(v * 100)}%");
            await Updater.InstallAsync(_update, p);
            UpdateLabel.Text = "Restarting";
            (Application.Current as App)?.Quit();
        }
        catch (Exception ex)
        {
            Log.Write("update failed: " + ex);
            Toast("Update failed: " + ex.Message);
            UpdateLabel.Text = "Try update again";
            UpdateBtn.IsEnabled = true;
            _busy = false;
        }
    }

    async Task CheckUpdate(bool silent)
    {
        if (_busy) return;
        _busy = true;
        if (!silent) UpdateLabel.Text = "Checking";
        try
        {
            _update = await Updater.CheckAsync();
            if (_update != null)
            {
                UpdateLabel.Text = "Update to " + _update.Tag;
                UpdateBtn.Style = (Style)FindResource("PrimaryButton");
                if (silent) Toast("A new version is available: " + _update.Tag);
            }
            else
            {
                UpdateLabel.Text = silent ? "Check for updates" : "You have the latest version";
            }
        }
        catch (Exception ex)
        {
            Log.Write("update check failed: " + ex.Message);
            UpdateLabel.Text = "Check for updates";
            if (!silent) Toast("Could not check for updates. Are you online?");
        }
        finally { _busy = false; }
    }

    void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Log.Write("log opened by user");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.Path) { UseShellExecute = true });
        }
        catch { Toast("No log yet. Apply to desktop first"); }
    }

    void OnStartSwitch(object sender, RoutedEventArgs e) => _app.SetAutostart(StartSwitch.IsChecked == true);

    async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_bmp == null) { Toast("Choose a wallpaper first"); return; }
        if (!E.IsCalibrated) { Toast("Mark both eyes first"); return; }
        _save.Stop(); _app.Save();
        Mouse.OverrideCursor = Cursors.Wait;
        var ok = await _app.ApplyAsync();
        Mouse.OverrideCursor = null;
        TrackSwitch.IsChecked = _app.Settings.TrackingEnabled && ok;
        Toast(ok ? "Applied. Show the desktop (Win+D) and move your mouse" : _app.Host.Error ?? "Could not attach to the desktop");
    }

    void OnReset(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this, "This removes your wallpaper, eye marks and preferences, and restores your previous Windows wallpaper.",
            "Reset settings?", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (r != MessageBoxResult.OK) return;
        _app.Reset();
        var d = _app.Settings.Eyes;
        Bind2(SlWidth, d.EyeWidth); Bind2(SlHeight, d.EyeHeight); Bind2(SlIris, d.IrisSize); Bind2(SlPupil, d.PupilSize);
        Bind2(SlTilt, d.Tilt); Bind2(SlLid, d.LidShadow); Bind2(SlSens, d.Sensitivity); Bind2(SlRange, d.Range); Bind2(SlSmooth, d.Smoothness);
        SyncAppearance(); ApplyCyclopsUi();
        BlinkSwitch.IsChecked = false; TrackSwitch.IsChecked = false; StartSwitch.IsChecked = false;
        LoadImage();
        Toast("Settings reset");
    }

    static void Bind2(LabeledSlider s, double v) => s.Value = v;

    // ---- status + toast ---------------------------------------------------------------------------------------

    void UpdateStatus()
    {
        var (text, color) = _app.Status switch
        {
            TrackingStatus.Watching => ("Watching your cursor", (Brush)FindResource("Accent")),
            TrackingStatus.Paused => ("Paused while windows cover the desktop", (Brush)FindResource("TextMuted")),
            TrackingStatus.Off => ("Eye tracking is off", (Brush)FindResource("TextMuted")),
            TrackingStatus.Error => (_app.LastError ?? "Could not attach to the desktop", new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x62))),
            _ => ("Not applied yet", (Brush)FindResource("TextMuted")),
        };
        StatusText.Text = text;
        StatusDot.Fill = color;
    }

    void Toast(string text)
    {
        ToastText.Text = text;
        var sb = new Storyboard();
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140))));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3200))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3500))));
        Storyboard.SetTarget(a, ToastBox);
        Storyboard.SetTargetProperty(a, new PropertyPath(OpacityProperty));
        sb.Children.Add(a);
        sb.Begin();
    }

}
