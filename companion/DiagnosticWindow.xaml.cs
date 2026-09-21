using System.Windows;

namespace CodexWallpaperSkin;

public partial class DiagnosticWindow : Window
{
    public DiagnosticWindow(string report)
    {
        InitializeComponent();
        UiLanguage.Apply(this);
        ReportTextBox.Text = report;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(ReportTextBox.Text);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
