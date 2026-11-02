using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace AgentBridge.Logging;

/// <summary>Where the file log goes and how long its files are kept.</summary>
internal readonly record struct FileLogSettings(string Folder, int RetentionDays);

/// <summary>
/// Writes every entry to {folder}/agentbridge-yyyyMMdd.log, one file per local calendar day, one line per entry:
/// <c>2026-11-02T10:15:30.123+07:00 INFO  AgentBridge.Processing.RequestProcessor: message</c>, an exception on the
/// lines after it. The folder is resolved on the first entry, not when the provider is created, so a host with invalid
/// configuration still builds and Program.cs can report the configuration error. A logging failure never reaches the caller.
/// </summary>
[ProviderAlias("File")]
internal sealed class FileLoggerProvider : ILoggerProvider
{
    internal const string FilePrefix = "agentbridge-";
    internal const string FileExtension = ".log";
    private const string DayFormat = "yyyyMMdd";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<FileLogSettings> _resolveSettings;
    private readonly TimeProvider _time;
    private readonly Action<string> _reportFailure;
    private readonly object _gate = new();
    private FileLogSettings? _settings;
    private StreamWriter? _writer;
    private DateOnly _day;
    private bool _disabled;
    private bool _failureReported;
    private bool _disposed;

    /// <param name="resolveSettings">Called on the first entry. Throwing <see cref="OptionsValidationException"/> switches the log off quietly.</param>
    /// <param name="reportFailure">Receives the one line about a log that cannot be written; stderr by default.</param>
    public FileLoggerProvider(Func<FileLogSettings> resolveSettings, TimeProvider timeProvider, Action<string>? reportFailure = null)
    {
        _resolveSettings = resolveSettings;
        _time = timeProvider;
        _reportFailure = reportFailure ?? Console.Error.WriteLine;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseWriter();
        }
    }

    /// <summary>The day in a file name written by this provider, or false for any other name.</summary>
    internal static bool TryParseDay(string fileName, out DateOnly day)
    {
        day = default;
        return fileName.Length == FilePrefix.Length + DayFormat.Length + FileExtension.Length
            && fileName.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase)
            && DateOnly.TryParseExact(fileName.AsSpan(FilePrefix.Length, DayFormat.Length), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var now = _time.GetLocalNow();
        var entry = Format(now, category, level, message, exception);
        lock (_gate)
        {
            if (_disposed || _disabled)
            {
                return;
            }
            try
            {
                var settings = _settings ??= _resolveSettings();
                var day = DateOnly.FromDateTime(now.DateTime);
                if (_writer is null || day != _day)
                {
                    OpenFor(settings, day);
                }
                _writer!.Write(entry);
            }
            catch (OptionsValidationException)
            {
                // Invalid configuration: Program.cs reports it, and nothing may be written until it is fixed.
                _disabled = true;
            }
            catch (Exception ex)
            {
                // The next entry tries again; only the first failure is reported.
                CloseWriter();
                ReportFailure(ex);
            }
        }
    }

    private static string Format(DateTimeOffset now, string category, LogLevel level, string message, Exception? exception)
    {
        var text = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture))
            .Append(' ').Append(LevelText(level))
            .Append(' ').Append(category)
            .Append(": ").Append(message)
            .AppendLine();
        if (exception is not null)
        {
            text.Append(exception).AppendLine();
        }
        return text.ToString();
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT ",
        _ => "NONE ",
    };

    /// <summary>Opens the file of the given day for appending, still readable by others, and drops the files that expired.</summary>
    private void OpenFor(FileLogSettings settings, DateOnly day)
    {
        CloseWriter();
        Directory.CreateDirectory(settings.Folder);
        var path = Path.Combine(settings.Folder, FilePrefix + day.ToString(DayFormat, CultureInfo.InvariantCulture) + FileExtension);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, Utf8) { AutoFlush = true };
        _day = day;
        DeleteExpiredFiles(settings, day);
    }

    /// <summary>Deletes agentbridge-yyyyMMdd.log files dated more than RetentionDays before today. Any other file stays.</summary>
    private static void DeleteExpiredFiles(FileLogSettings settings, DateOnly today)
    {
        var oldestKept = today.AddDays(-settings.RetentionDays);
        try
        {
            var expired = Directory.EnumerateFiles(settings.Folder, FilePrefix + "*" + FileExtension)
                .Where(file => TryParseDay(Path.GetFileName(file), out var day) && day < oldestKept)
                .ToList();
            foreach (var file in expired)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void CloseWriter()
    {
        var writer = _writer;
        _writer = null;
        try
        {
            writer?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private void ReportFailure(Exception ex)
    {
        if (_failureReported)
        {
            return;
        }
        _failureReported = true;
        try
        {
            _reportFailure($"AgentBridge: cannot write the log file ({ex.GetType().Name}: {ex.Message}). The worker keeps running.");
        }
        catch (Exception)
        {
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            try
            {
                _provider.Write(_category, logLevel, formatter(state, exception), exception);
            }
            catch (Exception)
            {
                // Logging must never break the code that logs.
            }
        }
    }
}
