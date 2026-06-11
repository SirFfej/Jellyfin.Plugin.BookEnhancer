using System.Text;

namespace Jellyfin.Plugin.BookEnhancer.Services.Tasks;

public sealed class TaskLogger : IProgress<double>, IDisposable
{
    private readonly string _logFilePath;
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private int _lastReportedPercent = -1;
    private bool _disposed;

    public TaskLogger(string logDirectory, string taskName, bool useDailyFile = false)
    {
        Directory.CreateDirectory(logDirectory);

        var suffix = useDailyFile
            ? DateTime.Now.ToString("yyyyMMdd")
            : DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _logFilePath = Path.Combine(logDirectory, $"{taskName}-{suffix}.log");
        _writer = new StreamWriter(_logFilePath, append: true, Encoding.UTF8) { AutoFlush = true };

        Write(LogLevel.Information, "Task started");
    }

    private enum LogLevel
    {
        Information,
        Warning,
        Error
    }

    public string LogFilePath => _logFilePath;

    public void LogInformation(string message)
    {
        Write(LogLevel.Information, message);
    }

    public void LogWarning(string message)
    {
        Write(LogLevel.Warning, message);
    }

    public void LogError(string message)
    {
        Write(LogLevel.Error, message);
    }

    public void LogError(Exception ex, string message)
    {
        Write(LogLevel.Error, $"{message}{Environment.NewLine}{ex}");
    }

    void IProgress<double>.Report(double value)
    {
        var pct = (int)(value * 100);
        if (pct != _lastReportedPercent)
        {
            _lastReportedPercent = pct;
            Write(LogLevel.Information, $"Progress: {pct}%");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Write(LogLevel.Information, "Task completed");
        _writer.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Write(LogLevel level, string message)
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level,-11}] {message}");
        }
    }
}
