using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace CodexWallpaperSkin;

public partial class NumericInputDialog : Window
{
    private readonly double _minimum;
    private readonly double _maximum;
    private readonly int _decimals;

    public double Value { get; private set; }

    public NumericInputDialog(string label, double current, double minimum, double maximum, string unit, int decimals)
    {
        InitializeComponent();
        _minimum = minimum;
        _maximum = maximum;
        _decimals = decimals;
        PromptText.Text = UiLanguage.Text(label);
        RangeText.Text = UiLanguage.IsChinese
            ? $"允许范围：{Format(minimum)}–{Format(maximum)}{unit}"
            : $"Allowed range: {Format(minimum)}–{Format(maximum)}{unit}";
        ValueTextBox.Text = Format(current);
        UiLanguage.Apply(this);
        Loaded += (_, _) =>
        {
            ValueTextBox.Focus();
            ValueTextBox.SelectAll();
        };
    }

    private void ValueTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Digits and a single decimal separator are accepted. Final parsing
        // still validates the complete value and range before it is applied.
        e.Handled = e.Text.Any(character => !char.IsDigit(character) && character is not '.' and not ',');
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var text = ValueTextBox.Text.Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            ShowValidation(UiLanguage.Text("Enter a valid number."));
            return;
        }
        if (value < _minimum || value > _maximum)
        {
            ShowValidation(UiLanguage.IsChinese
                ? $"请输入 {Format(_minimum)} 到 {Format(_maximum)} 之间的数值。"
                : $"Enter a value from {Format(_minimum)} to {Format(_maximum)}.");
            return;
        }
        Value = Math.Round(value, _decimals, MidpointRounding.AwayFromZero);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowValidation(string message)
    {
        MessageBox.Show(this, message, UiLanguage.Text("Exact value"), MessageBoxButton.OK, MessageBoxImage.Information);
        ValueTextBox.Focus();
        ValueTextBox.SelectAll();
    }

    private string Format(double value) => value.ToString(_decimals == 0 ? "0" : "0.##", CultureInfo.InvariantCulture);
}
