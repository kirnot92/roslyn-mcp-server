using Microsoft.Extensions.Logging;

namespace RoslynMcpServer.Infrastructure;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly LogLevel _minimumLevel;
    private readonly object _lock = new();

    public FileLoggerProvider(string path, LogLevel minimumLevel)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
        _minimumLevel = minimumLevel;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _lock, _minimumLevel);

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger(
        string categoryName,
        StreamWriter writer,
        object syncRoot,
        LogLevel minimumLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            lock (syncRoot)
            {
                writer.Write(DateTimeOffset.UtcNow.ToString("O"));
                writer.Write(' ');
                writer.Write(logLevel);
                writer.Write(' ');
                writer.Write(categoryName);
                writer.Write(": ");
                writer.WriteLine(formatter(state, exception));
                if (exception is not null)
                {
                    writer.WriteLine(exception);
                }
            }
        }
    }
}
