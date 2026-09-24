using System.Reflection;

namespace CodexWallpaperSkin;

public static class AppLog
{
    private const long MaximumLogBytes = 2L * 1024 * 1024;
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(StateStore.StateDirectory, "controller.log");
    public static string PreviousLogPath { get; } = Path.Combine(StateStore.StateDirectory, "controller.previous.log");

    public static void Initialize()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(StateStore.StateDirectory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaximumLogBytes)
                {
                    if (File.Exists(PreviousLogPath)) File.Delete(PreviousLogPath);
                    File.Move(LogPath, PreviousLogPath);
                }
                WriteCore("INFO", $"controller-start version={Assembly.GetExecutingAssembly().GetName().Version}");
            }
            catch
            {
                // Logging must never prevent startup or wallpaper cleanup.
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warning(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(Exception exception, string operation) =>
        Write("ERROR", $"{operation}: {exception.GetType().Name}: {exception.Message}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(StateStore.StateDirectory);
                WriteCore(level, message);
            }
            catch
            {
                // The controller remains usable if the log directory is locked.
            }
        }
    }

    private static void WriteCore(string level, string message)
    {
        var singleLine = string.Join(' ', (message ?? string.Empty)
            .Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        File.AppendAllText(
            LogPath,
            $"{DateTimeOffset.Now:O} [{level}] {singleLine}{Environment.NewLine}");
    }
}
