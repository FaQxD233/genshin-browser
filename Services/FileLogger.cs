using System.IO;

namespace GenshinBrowser.Services;

public static class FileLogger
{
    private static readonly object SyncRoot = new();

    public static void LogException(Exception exception, string context)
    {
        try
        {
            var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinBrowser");
            var logRoot = Path.Combine(dataRoot, "logs");
            Directory.CreateDirectory(logRoot);

            var logPath = Path.Combine(logRoot, $"{DateTime.Now:yyyy-MM-dd}.log");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";

            lock (SyncRoot)
            {
                File.AppendAllText(logPath, entry);
            }
        }
        catch
        {
            // Logging must never break browser workflows.
        }
    }
}
