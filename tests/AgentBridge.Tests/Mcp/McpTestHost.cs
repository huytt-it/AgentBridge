using System.Diagnostics;
using System.Text.Json;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;
using AgentBridge.Mcp;
using AgentBridge.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AgentBridge.Tests.Mcp;

/// <summary>
/// Options with a "fixture" MCP server (tests/AgentBridge.TestTools mcp-server), a manager and a factory.
/// Disposing it closes the servers and fails the test if any fixture process it started is still running.
/// </summary>
internal sealed class McpTestHost : IAsyncDisposable
{
    public const string Server = "fixture";

    /// <param name="options">Options to add the fixture server to, e.g. a PipelineHarness's; new options when null.</param>
    public McpTestHost(Action<AgentBridgeOptions>? configure = null, AgentBridgeOptions? options = null)
    {
        Options = options ?? new AgentBridgeOptions { AgentId = "test-agent" };
        Options.McpServers[Server] = FixtureServer();
        configure?.Invoke(Options);
        Manager = new McpServerManager(MsOptions.Create(Options), Logs);
        Factory = new McpCapabilityFactory(Manager, MsOptions.Create(Options), Logs);
    }

    public TempDirectory Root { get; } = new();
    public AgentBridgeOptions Options { get; }
    public ListLoggerFactory Logs { get; } = new();
    public McpServerManager Manager { get; }
    public McpCapabilityFactory Factory { get; }

    private string PidFolder => Directory.CreateDirectory(Root.Combine("pids")).FullName;

    /// <summary>
    /// The stand-in server; extra arguments such as "--hang" select a failure mode. It runs with the test assembly's
    /// runtime and dependency files: under net10.0 the test folder holds the net10.0 build of the MCP package, which
    /// the tools' own net8.0 files would load on the .NET 8 runtime and fail.
    /// </summary>
    public McpServerOptions FixtureServer(params string[] options) => new()
    {
        Command = TestSupport.TestTools.DotnetPath,
        Args =
        [
            "exec", "--runtimeconfig", TestAssemblyFile("runtimeconfig.json"), "--depsfile", TestAssemblyFile("deps.json"),
            .. TestSupport.TestTools.Args(["mcp-server", .. options]),
        ],
        Environment = { ["AGENTBRIDGE_FIXTURE_PIDS"] = PidFolder },
        StartupTimeoutSeconds = 30,
    };

    private static string TestAssemblyFile(string extension) =>
        Path.Combine(AppContext.BaseDirectory, $"{typeof(McpTestHost).Assembly.GetName().Name}.{extension}");

    public ICapability Create(string name, CapabilityOptions options)
    {
        var errors = new List<string>();
        var capability = Factory.Create(name, options, errors);
        Assert.True(capability is not null, string.Join(Environment.NewLine, errors));
        Assert.Empty(errors);
        return capability;
    }

    public List<string> CreateErrors(string name, CapabilityOptions options)
    {
        var errors = new List<string>();
        Assert.Null(Factory.Create(name, options, errors));
        Assert.NotEmpty(errors);
        return errors;
    }

    public CapabilityContext Context(string argsJson = "{}", string? targetPath = null)
    {
        var jobDirectory = Directory.CreateDirectory(Root.Combine("job", Guid.NewGuid().ToString("N"))).FullName;
        return new CapabilityContext
        {
            Request = new AgentRequest { SchemaVersion = 1, RequestId = "r1", Capability = "test.mcp" },
            Args = JsonSerializer.Deserialize<JsonElement>(argsJson),
            TargetName = targetPath is null ? null : "repo",
            TargetPath = targetPath,
            JobDirectory = jobDirectory,
            ArtifactDirectory = Directory.CreateDirectory(Path.Combine(jobDirectory, "artifacts")).FullName,
        };
    }

    /// <summary>Process ids of every fixture server started through this host so far.</summary>
    public IReadOnlyList<int> StartedPids() =>
        Directory.EnumerateFiles(PidFolder, "*.pid").Select(f => int.Parse(Path.GetFileNameWithoutExtension(f))).ToList();

    public static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        var running = StartedPids().Where(IsRunning).ToList();
        Root.Dispose();
        Assert.True(running.Count == 0, $"Fixture MCP server processes still running after dispose: {string.Join(", ", running)}");
    }
}

internal sealed record LoggedEntry(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>Collects every log entry of every category, for checks on what reaches the local log.</summary>
internal sealed class ListLoggerFactory : ILoggerFactory
{
    private readonly List<LoggedEntry> _entries = [];

    public IReadOnlyList<LoggedEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(ListLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._entries)
            {
                owner._entries.Add(new LoggedEntry(category, logLevel, formatter(state, exception), exception));
            }
        }
    }
}
