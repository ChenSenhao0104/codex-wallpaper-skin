using System.Windows;
using Microsoft.Win32;

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

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = UiLanguage.Text("Export diagnostic package"),
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"CodexWallpaperSkin-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            AddExtension = true,
            DefaultExt = ".zip",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SupportBundleService.Export(dialog.FileName, ReportTextBox.Text);
            MessageBox.Show(
                this,
                UiLanguage.Text("Diagnostic package exported successfully."),
                "Codex Wallpaper Skin",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            AppLog.Error(exception, "support-bundle-export");
            MessageBox.Show(this, exception.Message, "Codex Wallpaper Skin", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
