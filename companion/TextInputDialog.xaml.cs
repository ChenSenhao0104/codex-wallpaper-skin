using System.Windows;
using System.Windows.Input;

namespace CodexWallpaperSkin;

public partial class TextInputDialog : Window
{
    public string? Result { get; private set; }
    public bool UseSecondaryAction { get; private set; }

    public TextInputDialog(
        Window owner,
        string title,
        string prompt,
        string currentValue,
        int maximumLength,
        string secondaryButtonText)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        PromptText.Text = prompt;
        ValueTextBox.Text = currentValue;
        ValueTextBox.MaxLength = maximumLength;
        SecondaryButton.Content = secondaryButtonText;
        Loaded += (_, _) =>
        {
            ValueTextBox.Focus();
            ValueTextBox.SelectAll();
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = ValueTextBox.Text;
        DialogResult = true;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        UseSecondaryAction = true;
        Result = null;
        DialogResult = true;
    }

    private void ValueTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
