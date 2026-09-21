using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace CodexWallpaperSkin;

public partial class VisualPresetDialog : Window
{
    private readonly List<VisualPresetProfile> _profiles;
    private VisualPresetProfile? _editingProfile;
    private bool _switching;

    public IReadOnlyList<VisualPresetProfile> Profiles => _profiles;
    public string SelectedProfileId { get; private set; }

    public VisualPresetDialog(IEnumerable<VisualPresetProfile> profiles, string? selectedProfileId)
    {
        InitializeComponent();
        _profiles = profiles.Select(item => item.Copy().Normalize()).ToList();
        if (_profiles.Count == 0) _profiles.Add(VisualPresetProfile.BuiltIn());
        SelectedProfileId = _profiles.Any(item => item.Id.Equals(selectedProfileId, StringComparison.OrdinalIgnoreCase))
            ? selectedProfileId!
            : _profiles[0].Id;
        PresetCombo.ItemsSource = _profiles;
        _switching = true;
        PresetCombo.SelectedItem = _profiles.First(item => item.Id.Equals(SelectedProfileId, StringComparison.OrdinalIgnoreCase));
        _switching = false;
        BeginEditing((VisualPresetProfile)PresetCombo.SelectedItem);
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switching || PresetCombo.SelectedItem is not VisualPresetProfile selected) return;
        if (!TryCommitCurrent())
        {
            _switching = true;
            PresetCombo.SelectedItem = _editingProfile;
            _switching = false;
            return;
        }
        BeginEditing(selected);
    }

    private void NewPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCommitCurrent()) return;
        if (_profiles.Count >= 32)
        {
            MessageBox.Show(this, "At most 32 visual presets can be saved.", "Manage visual presets",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var source = _editingProfile?.Settings ?? VisualPresetSettings.BuiltIn();
        var profile = new VisualPresetProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = NextPresetName(),
            Settings = CopySettings(source)
        };
        _profiles.Add(profile);
        PresetCombo.Items.Refresh();
        PresetCombo.SelectedItem = profile;
        PresetNameText.Focus();
        PresetNameText.SelectAll();
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_editingProfile is null) return;
        if (_profiles.Count == 1)
        {
            MessageBox.Show(this, "Keep at least one visual preset.", "Manage visual presets",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var choice = MessageBox.Show(this, $"Delete preset ‘{_editingProfile.Name}’?", "Delete visual preset",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes) return;

        var index = _profiles.IndexOf(_editingProfile);
        _switching = true;
        _profiles.RemoveAt(index);
        PresetCombo.Items.Refresh();
        PresetCombo.SelectedItem = _profiles[Math.Min(index, _profiles.Count - 1)];
        _switching = false;
        BeginEditing((VisualPresetProfile)PresetCombo.SelectedItem);
    }

    private void BuiltInDefaults_Click(object sender, RoutedEventArgs e) =>
        LoadValues(VisualPresetSettings.BuiltIn());

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCommitCurrent()) return;
        SelectedProfileId = _editingProfile?.Id ?? _profiles[0].Id;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void BeginEditing(VisualPresetProfile profile)
    {
        _editingProfile = profile;
        SelectedProfileId = profile.Id;
        PresetNameText.Text = profile.Name;
        LoadValues(profile.Settings);
    }

    private bool TryCommitCurrent()
    {
        if (_editingProfile is null) return true;
        try
        {
            var name = PresetNameText.Text.Trim();
            if (name.Length is < 1 or > 64)
                throw new InvalidDataException("Preset name must contain 1 to 64 characters.");
            if (_profiles.Any(item => !ReferenceEquals(item, _editingProfile)
                && item.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
            {
                throw new InvalidDataException("Preset names must be unique.");
            }
            _editingProfile.Name = name;
            _editingProfile.Settings = ReadValues();
            PresetCombo.Items.Refresh();
            return true;
        }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(this, exception.Message, "Manage visual presets", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
    }

    private VisualPresetSettings ReadValues() => new VisualPresetSettings
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

    private string NextPresetName()
    {
        for (var index = 1; index <= 32; index++)
        {
            var candidate = $"Preset {index}";
            if (_profiles.All(item => !item.Name.Equals(candidate, StringComparison.CurrentCultureIgnoreCase)))
                return candidate;
        }
        return "New preset";
    }

    private static double ReadPercent(WpfTextBox textBox, string label, double minimum, double maximum) =>
        ReadNumber(textBox, label, minimum, maximum) / 100;

    private static double ReadNumber(WpfTextBox textBox, string label, double minimum, double maximum)
    {
        var text = textBox.Text.Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < minimum || value > maximum)
        {
            textBox.Focus();
            textBox.SelectAll();
            throw new InvalidDataException($"{label} must be from {minimum:0.##} to {maximum:0.##}.");
        }
        return value;
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static VisualPresetSettings CopySettings(VisualPresetSettings value) => new()
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
