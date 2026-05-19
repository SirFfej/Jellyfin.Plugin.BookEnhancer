using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Logging;

public class BooksFileLoggerProvider : ILoggerProvider
{
    public BooksFileLoggerProvider(string logDirectoryPath) { }
    public ILogger CreateLogger(string categoryName) => new EmptyLogger();
    public void Dispose() { }

    private class EmptyLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
