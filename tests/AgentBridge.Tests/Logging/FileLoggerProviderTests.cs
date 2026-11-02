using System.Text.RegularExpressions;
using AgentBridge.Logging;
using AgentBridge.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AgentBridge.Tests.Logging;

public sealed class FileLoggerProviderTests : IDisposable
{
    private const string Category = "AgentBridge.Processing.RequestProcessor";
    private const string FirstTimestamp = "2026-11-02T10:15:30.123+07:00";

    private readonly TempDirectory _root = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 2, 3, 15, 30, 123, TimeSpan.Zero)); // 10:15:30.123 in the UTC+7 zone set below
    private readonly List<string> _failures = [];
    private readonly List<IDisposable> _disposables = [];

    public FileLoggerProviderTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("UTC+7", TimeSpan.FromHours(7), "UTC+7", "UTC+7"));
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
        _root.Dispose();
    }

    private string Folder => _root.Combine("logs");

    private FileLoggerProvider Create(int retentionDays = 14, string? folder = null)
    {
        var settings = new FileLogSettings(folder ?? Folder, retentionDays);
        var provider = new FileLoggerProvider(() => settings, _time, _failures.Add);
        _disposables.Add(provider);
        return provider;
    }

    /// <summary>Reads like a tail would: the provider still has the file open.</summary>
    private List<string> ReadLines(string fileName)
    {
        using var stream = new FileStream(Path.Combine(Folder, fileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }
        return lines;
    }

    private string[] LogFiles() =>
        Directory.GetFiles(Folder).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();

    private void Touch(string fileName) => File.WriteAllText(Path.Combine(Folder, fileName), "old");

    [Fact]
    public void Writes_one_line_per_entry_in_the_file_of_the_local_day()
    {
        var logger = Create().CreateLogger(Category);

        logger.LogInformation("Request {RequestId} succeeded.", "r1");
        _time.Advance(TimeSpan.FromSeconds(1));
        logger.LogWarning("Second entry.");

        Assert.Equal(["agentbridge-20261102.log"], LogFiles());
        Assert.Equal(
            [
                $"{FirstTimestamp} INFO  {Category}: Request r1 succeeded.",
                $"2026-11-02T10:15:31.123+07:00 WARN  {Category}: Second entry.",
            ],
            ReadLines("agentbridge-20261102.log"));
    }

    [Fact]
    public void The_day_is_the_local_calendar_day_not_the_utc_day()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 2, 20, 0, 0, TimeSpan.Zero));
        var logger = Create().CreateLogger(Category);

        logger.LogInformation("Late evening in UTC, already tomorrow here.");

        Assert.Equal(["agentbridge-20261103.log"], LogFiles());
        Assert.StartsWith("2026-11-03T03:00:00.000+07:00 ", Assert.Single(ReadLines("agentbridge-20261103.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void Starts_a_new_file_when_the_day_changes()
    {
        var logger = Create().CreateLogger(Category);
        logger.LogInformation("Before midnight.");

        _time.Advance(TimeSpan.FromHours(14)); // 00:15:30 the next day
        logger.LogInformation("After midnight.");
        logger.LogInformation("Still after midnight.");

        Assert.Equal(["agentbridge-20261102.log", "agentbridge-20261103.log"], LogFiles());
        Assert.EndsWith("Before midnight.", Assert.Single(ReadLines("agentbridge-20261102.log")), StringComparison.Ordinal);
        Assert.Equal(2, ReadLines("agentbridge-20261103.log").Count);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "TRACE")]
    [InlineData(LogLevel.Debug, "DEBUG")]
    [InlineData(LogLevel.Information, "INFO ")]
    [InlineData(LogLevel.Warning, "WARN ")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Critical, "CRIT ")]
    public void Pads_the_level_to_five_characters(LogLevel level, string expected)
    {
        Create().CreateLogger("Cat").Log(level, "message");

        Assert.Equal($"{FirstTimestamp} {expected} Cat: message", Assert.Single(ReadLines("agentbridge-20261102.log")));
    }

    [Fact]
    public void Writes_the_exception_on_the_lines_after_the_message()
    {
        var logger = Create().CreateLogger(Category);
        Exception exception;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        logger.LogError(exception, "Inbox pass failed; retrying in {PollSeconds}s.", 5);
        logger.LogInformation("Next entry.");

        var lines = ReadLines("agentbridge-20261102.log");
        Assert.Equal($"{FirstTimestamp} ERROR {Category}: Inbox pass failed; retrying in 5s.", lines[0]);
        Assert.StartsWith("System.InvalidOperationException: boom", lines[1], StringComparison.Ordinal);
        Assert.Contains(lines.Skip(2), line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal));
        Assert.EndsWith($" INFO  {Category}: Next entry.", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_non_ascii_text_as_utf8_without_a_byte_order_mark()
    {
        var logger = Create().CreateLogger(Category);

        logger.LogInformation("Báo cáo");

        using var stream = new FileStream(Path.Combine(Folder, "agentbridge-20261102.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.NotEqual(0xEF, stream.ReadByte());
        Assert.Contains("Báo cáo", ReadLines("agentbridge-20261102.log").Single(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Logging:LogLevel:Default=Warning", "warning,error")]
    [InlineData("Logging:LogLevel:Default=Debug;Logging:File:LogLevel:Default=Warning", "warning,error")]
    [InlineData("Logging:LogLevel:Default=Error;Logging:File:LogLevel:Default=Information", "information,warning,error")]
    [InlineData("Logging:LogLevel:Default=Information;Logging:LogLevel:AgentBridge.Processing=Error", "error")]
    public void Follows_the_logging_configuration_of_the_file_provider(string settings, string expected)
    {
        var values = settings.Split(';').Select(s => s.Split('=')).ToDictionary(p => p[0], p => (string?)p[1]);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var factory = LoggerFactory.Create(builder => builder
            .AddConfiguration(configuration.GetSection("Logging"))
            .AddProvider(Create()));
        var logger = factory.CreateLogger(Category);

        logger.LogDebug("debug");
        logger.LogInformation("information");
        logger.LogWarning("warning");
        logger.LogError("error");

        var messages = ReadLines("agentbridge-20261102.log").Select(line => line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..]);
        Assert.Equal(expected, string.Join(",", messages));
    }

    [Fact]
    public void The_file_provider_is_configured_under_the_name_File()
    {
        var alias = Assert.Single(typeof(FileLoggerProvider).GetCustomAttributes(typeof(ProviderAliasAttribute), inherit: false));

        Assert.Equal("File", ((ProviderAliasAttribute)alias).Alias);
    }

    [Fact]
    public void Retention_deletes_only_old_log_files_of_this_logger()
    {
        Directory.CreateDirectory(Folder);
        // Today is 2026-11-02 and 14 days are kept: 2026-10-19 stays, 2026-10-18 goes.
        Touch("agentbridge-20261018.log");
        Touch("agentbridge-20200101.log");
        Touch("agentbridge-20261019.log");
        Touch("agentbridge-20261101.log");
        // Not ours, or not a daily log: untouched, however old.
        Touch("other-20200101.log");
        Touch("notes-agentbridge-20200101.log");
        Touch("agentbridge-notes.log");
        Touch("agentbridge-2020010.log");
        Touch("agentbridge-20200101.log.bak");
        Touch("agentbridge-20200101.txt");
        Touch("agentbridge-20201301.log");
        Directory.CreateDirectory(Path.Combine(Folder, "agentbridge-20200102.log"));

        Create(retentionDays: 14).CreateLogger(Category).LogInformation("Startup.");

        string[] expected =
        [
            "agentbridge-20200101.log.bak",
            "agentbridge-20200101.txt",
            "agentbridge-20200102.log", // a folder, not a file
            "agentbridge-2020010.log",
            "agentbridge-20201301.log",
            "agentbridge-20261019.log",
            "agentbridge-20261101.log",
            "agentbridge-20261102.log",
            "agentbridge-notes.log",
            "notes-agentbridge-20200101.log",
            "other-20200101.log",
        ];
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            Directory.GetFileSystemEntries(Folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Retention_runs_again_when_the_day_changes_and_never_deletes_the_current_file()
    {
        Directory.CreateDirectory(Folder);
        Touch("agentbridge-20261030.log");
        var logger = Create(retentionDays: 3).CreateLogger(Category);

        logger.LogInformation("Today.");
        Assert.Contains("agentbridge-20261030.log", LogFiles());

        _time.Advance(TimeSpan.FromHours(24));
        logger.LogInformation("Tomorrow.");

        Assert.Equal(["agentbridge-20261102.log", "agentbridge-20261103.log"], LogFiles());
    }

    [Fact]
    public void Retention_does_not_run_between_entries_of_one_day()
    {
        var logger = Create(retentionDays: 1).CreateLogger(Category);
        logger.LogInformation("Opens the file.");
        Touch("agentbridge-20200101.log");

        logger.LogInformation("Same day.");

        Assert.Contains("agentbridge-20200101.log", LogFiles());
    }

    [Fact]
    public void Concurrent_writers_never_mix_up_lines()
    {
        const int threads = 8;
        const int entries = 250;
        var logger = Create().CreateLogger("Concurrency");
        var padding = new string('x', 200);

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, thread =>
        {
            for (var i = 0; i < entries; i++)
            {
                logger.LogInformation("thread {Thread} entry {Entry} {Padding}", thread, i, padding);
            }
        });

        var lines = ReadLines("agentbridge-20261102.log");
        var pattern = new Regex(@"^2026-11-02T10:15:30\.123\+07:00 INFO  Concurrency: thread (\d+) entry (\d+) x{200}$");
        Assert.Equal(threads * entries, lines.Count);
        var seen = lines.Select(line => pattern.Match(line)).ToList();
        Assert.All(seen, match => Assert.True(match.Success));
        Assert.Equal(threads * entries, seen.Select(m => (m.Groups[1].Value, m.Groups[2].Value)).Distinct().Count());
    }

    [Fact]
    public void An_unwritable_folder_never_throws_and_is_reported_once()
    {
        var blocker = _root.Combine("blocked");
        File.WriteAllText(blocker, "a file where the log folder should be");
        var logger = Create(folder: Path.Combine(blocker, "logs")).CreateLogger(Category);

        var exception = Record.Exception(() =>
        {
            logger.LogInformation("One.");
            logger.LogError(new IOException("x"), "Two.");
            logger.LogCritical("Three.");
        });

        Assert.Null(exception);
        var report = Assert.Single(_failures);
        Assert.Contains("cannot write the log file", report, StringComparison.Ordinal);
        Assert.DoesNotContain("One.", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Writing_resumes_when_the_problem_goes_away()
    {
        var blocker = _root.Combine("logs");
        File.WriteAllText(blocker, "a file where the log folder should be");
        var logger = Create().CreateLogger(Category);
        logger.LogInformation("Lost.");

        File.Delete(blocker);
        logger.LogInformation("Kept.");

        Assert.Single(_failures);
        Assert.EndsWith("Kept.", Assert.Single(ReadLines("agentbridge-20261102.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failing_report_or_formatter_does_not_reach_the_caller()
    {
        var blocker = _root.Combine("blocked");
        File.WriteAllText(blocker, "x");
        var provider = new FileLoggerProvider(() => new FileLogSettings(Path.Combine(blocker, "logs"), 14), _time, _ => throw new InvalidOperationException("stderr is gone"));
        _disposables.Add(provider);
        var logger = provider.CreateLogger(Category);

        var exception = Record.Exception(() =>
        {
            logger.LogInformation("Cannot be written.");
            logger.Log(LogLevel.Information, new EventId(1), "state", null, (_, _) => throw new FormatException("bad format"));
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Invalid_configuration_switches_the_log_off_without_a_sound()
    {
        var resolved = 0;
        var provider = new FileLoggerProvider(
            () =>
            {
                resolved++;
                throw new OptionsValidationException("AgentBridge", typeof(AgentBridgeOptions), ["AgentBridge:PollSeconds must be between 1 and 3600."]);
            },
            _time,
            _failures.Add);
        _disposables.Add(provider);
        var logger = provider.CreateLogger(Category);

        logger.LogInformation("One.");
        logger.LogInformation("Two.");

        Assert.Empty(_failures);
        Assert.Equal(1, resolved);
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public void The_folder_is_resolved_on_the_first_entry_not_when_the_provider_is_created()
    {
        var resolved = 0;
        var provider = new FileLoggerProvider(() => { resolved++; return new FileLogSettings(Folder, 14); }, _time, _failures.Add);
        _disposables.Add(provider);

        var logger = provider.CreateLogger(Category);
        Assert.Equal(0, resolved);

        logger.LogInformation("First.");
        logger.LogInformation("Second.");
        Assert.Equal(1, resolved);
    }

    [Fact]
    public void The_file_can_be_read_while_the_provider_has_it_open()
    {
        var logger = Create().CreateLogger(Category);

        logger.LogInformation("First.");
        Assert.Single(ReadLines("agentbridge-20261102.log"));

        // A second reader at the same time, as a tail or an editor would: nothing waits for the provider to close the file.
        using var reader = new FileStream(Path.Combine(Folder, "agentbridge-20261102.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        logger.LogInformation("Second.");
        Assert.Equal(2, ReadLines("agentbridge-20261102.log").Count);
    }

    [Fact]
    public void Appends_to_the_file_of_an_earlier_run_the_same_day()
    {
        var firstRun = Create();
        firstRun.CreateLogger(Category).LogInformation("First run.");
        firstRun.Dispose();

        Create().CreateLogger(Category).LogInformation("Second run.");

        var lines = ReadLines("agentbridge-20261102.log");
        Assert.EndsWith("First run.", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("Second run.", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_disposed_provider_ignores_entries()
    {
        var provider = Create();
        var logger = provider.CreateLogger(Category);
        logger.LogInformation("Before.");
        provider.Dispose();

        var exception = Record.Exception(() => logger.LogInformation("After."));

        Assert.Null(exception);
        Assert.Single(ReadLines("agentbridge-20261102.log"));
        provider.Dispose();
    }
}
