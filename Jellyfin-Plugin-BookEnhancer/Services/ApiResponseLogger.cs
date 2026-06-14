using System.Text;

namespace Jellyfin.Plugin.BookEnhancer.Services;

/// <summary>
/// Static logger for detailed API request/response information.
/// Keeps the main Jellyfin log clean by writing API responses and exceptions
/// to a dedicated rolling file under the plugin data directory.
/// </summary>
public static class ApiResponseLogger
{
    private static readonly object _lock = new();
    private static string? _logDirectory;

    private static string LogDirectory
    {
        get
        {
            if (_logDirectory is null)
            {
                var dataPath = Plugin.DataPath;
                _logDirectory = string.IsNullOrWhiteSpace(dataPath)
                    ? Path.Combine(Path.GetTempPath(), "BookEnhancer", "api-responses")
                    : Path.Combine(dataPath, "plugins", "BookEnhancer", "api-responses");
            }

            return _logDirectory;
        }
    }

    private static string LogFilePath
    {
        get
        {
            Directory.CreateDirectory(LogDirectory);
            return Path.Combine(LogDirectory, $"api-responses-{DateTime.Now:yyyyMMdd}.log");
        }
    }

    public static void Log(string source, string message)
    {
        Write($"[{source}] {message}");
    }

    public static void Log(string source, string message, Exception ex)
    {
        Write($"[{source}] {message}{Environment.NewLine}{ex}");
    }

    private static void Write(string message)
    {
        lock (_lock)
        {
            try
            {
                using var writer = new StreamWriter(LogFilePath, append: true, Encoding.UTF8) { AutoFlush = true };
                writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
            }
            catch
            {
                // Best-effort logging: don't throw if the dedicated log file cannot be written.
            }
        }
    }
}
