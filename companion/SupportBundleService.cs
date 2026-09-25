using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace CodexWallpaperSkin;

public static class SupportBundleService
{
    public static void Export(string destinationPath, string diagnosticJson)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("Choose a destination for the diagnostic package.", nameof(destinationPath));

        var fullPath = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The diagnostic package destination has no parent folder.");
        Directory.CreateDirectory(parent);
        var temporaryPath = Path.Combine(parent, $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteText(archive, "diagnostic.json", DiagnosticPrivacy.RedactText(diagnosticJson) ?? string.Empty);
                WriteText(
                    archive,
                    "README.txt",
                    "Codex Wallpaper Skin local diagnostic package\r\n"
                    + $"Generated: {DateTimeOffset.Now:O}\r\n"
                    + $"Version: {Assembly.GetExecutingAssembly().GetName().Version}\r\n\r\n"
                    + "This package contains a shareable Doctor report and redacted controller logs. "
                    + "Local paths, user names, page identifiers, wallpaper media, authentication data, conversation content, and the complete saved state file are excluded.\r\n");
                AddRedactedLogIfPresent(archive, AppLog.LogPath, "controller.log");
                AddRedactedLogIfPresent(archive, AppLog.PreviousLogPath, "controller.previous.log");
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
            AppLog.Info("support-bundle-exported");
        }
        catch
        {
            try { File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    private static void AddRedactedLogIfPresent(ZipArchive archive, string path, string entryName)
    {
        if (!File.Exists(path)) return;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var redacted = DiagnosticPrivacy.RedactText(reader.ReadToEnd()) ?? string.Empty;
        WriteText(archive, entryName, redacted);
    }

    private static void WriteText(ZipArchive archive, string name, string value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(value);
    }
}
