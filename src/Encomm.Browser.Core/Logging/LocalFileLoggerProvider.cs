using Microsoft.Extensions.Logging;

namespace Encomm.Browser.Core;

/// <summary>
/// A minimal structured logger that writes to a bounded rolling file.
/// Used as the default local logger when nothing else is configured.
/// </summary>
public sealed class LocalFileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _gate = new();

    public LocalFileLoggerProvider(string filePath)
    {
        _filePath = filePath;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_filePath, line + Environment.NewLine);
                RollIfNeeded();
            }
            catch
            {
                // Logging must never throw into app code.
            }
        }
    }

    private void RollIfNeeded()
    {
        try
        {
            var info = new FileInfo(_filePath);
            if (!info.Exists) return;
            if (info.Length <= 5 * 1024 * 1024) return; // 5 MB cap
            var backup = _filePath + ".1";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(_filePath, backup);
        }
        catch { }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly LocalFileLoggerProvider _owner;
        private readonly string _category;

        public FileLogger(LocalFileLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            // Scrub common secret patterns defensively.
            msg = SecretScrubber.Scrub(msg);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {_category}: {msg}";
            if (exception is not null) line += " | " + exception.GetType().Name + ": " + exception.Message;
            _owner.Write(line);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

internal static class SecretScrubber
{
    private static readonly string[] Keys =
    {
        "api-key", "apikey", "authorization", "bearer ", "x-api-key", "openai-api-key", "password", "secret", "token"
    };

    public static string Scrub(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var lower = input.ToLowerInvariant();
        foreach (var key in Keys)
        {
            var idx = lower.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) continue;
            // Replace the value after the key= or key: with ***.
            var sepIdx = input.IndexOfAny(new[] { '=', ':' }, idx);
            if (sepIdx < 0) continue;
            var endIdx = input.IndexOfAny(new[] { ',', ';', '\n', '\r', '"', '\'' }, sepIdx + 1);
            if (endIdx < 0) endIdx = input.Length;
            input = input.Substring(0, sepIdx + 1) + " ***" + input.Substring(endIdx);
            lower = input.ToLowerInvariant();
        }
        return input;
    }
}