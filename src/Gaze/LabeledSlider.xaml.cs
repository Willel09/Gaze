using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Gaze;

public partial class LabeledSlider : UserControl
{
    bool _silent;

    public LabeledSlider() { InitializeComponent(); }

    public string Label { set => LabelText.Text = value; }
    public double Minimum { set => S.Minimum = value; }
    public double Maximum { set { S.Maximum = value; S.SmallChange = (value - S.Minimum) / 100; S.LargeChange = (value - S.Minimum) / 10; } }
    /// <summary>Displayed value = Value * Scale, followed by Unit.</summary>
    public double Scale { get; set; } = 100;
    public string Unit { get; set; } = "%";
    public event Action<double>? Changed;

    public double Value
    {
        get => S.Value;
        set { _silent = true; S.Value = value; ShowValue(); _silent = false; }
    }

    void ShowValue() => ValueText.Text = (S.Value * Scale).ToString("0", CultureInfo.CurrentCulture) + Unit;

    void OnChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ShowValue();
        if (!_silent) Changed?.Invoke(e.NewValue);
    }
}
