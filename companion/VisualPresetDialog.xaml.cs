using System.Globalization;
using System.Windows;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace CodexWallpaperSkin;

public partial class VisualPresetDialog : Window
{
    public VisualPresetSettings Preset { get; private set; }

    public VisualPresetDialog(VisualPresetSettings preset)
    {
        InitializeComponent();
        Preset = Copy(preset).Normalize();
        LoadValues(Preset);
    }

    private void BuiltInDefaults_Click(object sender, RoutedEventArgs e) =>
        LoadValues(VisualPresetSettings.BuiltIn());

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Preset = new VisualPresetSettings
            {
                Opacity = ReadPercent(OpacityText, "Background opacity", 0, 100),
                BlackOverlay = ReadPercent(OverlayText, "Black readability overlay", 0, 80),
                Brightness = ReadPercent(BrightnessText, "Brightness", 50, 150),
                Contrast = ReadPercent(ContrastText, "Contrast", 50, 150),
                Saturation = ReadPercent(SaturationText, "Saturation", 0, 200),
                PanelOpacity = ReadPercent(PanelOpacityText, "Panel opacity", 20, 95),
                Blur = ReadNumber(BlurText, "Blur", 0, 30),
                SceneResolutionScale = ReadPercent(SceneScaleText, "Scene capture scale", 50, 100)
            }.Normalize();
            DialogResult = true;
        }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(this, exception.Message, "Visual preset settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void LoadValues(VisualPresetSettings preset)
    {
        OpacityText.Text = Format(preset.Opacity * 100);
        OverlayText.Text = Format(preset.BlackOverlay * 100);
        BrightnessText.Text = Format(preset.Brightness * 100);
        ContrastText.Text = Format(preset.Contrast * 100);
        SaturationText.Text = Format(preset.Saturation * 100);
        PanelOpacityText.Text = Format(preset.PanelOpacity * 100);
        BlurText.Text = Format(preset.Blur);
        SceneScaleText.Text = Format(preset.SceneResolutionScale * 100);
    }

    private static double ReadPercent(WpfTextBox textBox, string label, double minimum, double maximum) =>
        ReadNumber(textBox, label, minimum, maximum) / 100;

    private static double ReadNumber(WpfTextBox textBox, string label, double minimum, double maximum)
    {
        var text = textBox.Text.Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value)
            || value < minimum
            || value > maximum)
        {
            textBox.Focus();
            textBox.SelectAll();
            throw new InvalidDataException($"{label} must be from {minimum:0.##} to {maximum:0.##}.");
        }
        return value;
    }

    private static string Format(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static VisualPresetSettings Copy(VisualPresetSettings value) => new()
    {
        Opacity = value.Opacity,
        BlackOverlay = value.BlackOverlay,
        Brightness = value.Brightness,
        Contrast = value.Contrast,
        Saturation = value.Saturation,
        PanelOpacity = value.PanelOpacity,
        Blur = value.Blur,
        SceneResolutionScale = value.SceneResolutionScale
    };
}
