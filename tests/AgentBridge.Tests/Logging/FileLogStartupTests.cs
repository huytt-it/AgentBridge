using System.Diagnostics;
using AgentBridge.Logging;
using AgentBridge.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentBridge.Tests.Logging;

/// <summary>The file log as the worker wires it: configuration decides where it goes, and a bad configuration is reported by Program.cs.</summary>
public sealed class FileLogStartupTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private string LogFolder => _root.Combine("data", "logs");

    private IHost BuildHost(params (string Key, string Value)[] settings)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _root.Path });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentBridge:BoxRoot"] = _root.Combine("box"),
            ["AgentBridge:LocalDataRoot"] = _root.Combine("data"),
        });
        builder.Configuration.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value));
        builder.Services.AddAgentBridge(builder.Configuration);
        builder.Services.AddAgentBridgeFileLog();
        return builder.Build();
    }

    [Fact]
    public void Writes_to_the_logs_folder_of_the_local_data_root()
    {
        using var host = BuildHost();

        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Demo").LogInformation("Hello from the worker.");

        var file = Assert.Single(Directory.GetFiles(LogFolder, "agentbridge-*.log"));
        Assert.Equal(host.Services.GetRequiredService<AgentBridgePaths>().Logs, LogFolder);
        Assert.EndsWith(" INFO  Demo: Hello from the worker.", Assert.Single(ReadLines(file)), StringComparison.Ordinal);
    }

    [Fact]
    public void Uses_the_configured_retention()
    {
        Directory.CreateDirectory(LogFolder);
        File.WriteAllText(Path.Combine(LogFolder, "agentbridge-20200101.log"), "old");
        File.WriteAllText(Path.Combine(LogFolder, "agentbridge-20200101.txt"), "not a log of ours");
        using var host = BuildHost(("AgentBridge:LogRetentionDays", "1"));

        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Demo").LogInformation("Startup.");

        Assert.False(File.Exists(Path.Combine(LogFolder, "agentbridge-20200101.log")));
        Assert.True(File.Exists(Path.Combine(LogFolder, "agentbridge-20200101.txt")));
    }

    [Fact]
    public void Invalid_configuration_still_builds_the_host_and_writes_no_log()
    {
        using var host = BuildHost(("AgentBridge:PollSeconds", "0"));

        var exception = Record.Exception(() =>
        {
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Demo");
            logger.LogInformation("Before the configuration is reported.");
            logger.LogError("Again.");
        });

        Assert.Null(exception);
        // This is the exception Program.cs turns into "Configuration is invalid:".
        Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<AgentBridgePaths>());
        Assert.False(Directory.Exists(_root.Combine("data")));
    }

    [Fact]
    public void Never_writes_inside_the_box_root()
    {
        var inBox = _root.Combine("box", "data");
        using var host = BuildHost(("AgentBridge:LocalDataRoot", inBox));

        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Demo").LogInformation("Must not land in Box.");

        Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<AgentBridgePaths>());
        Assert.False(Directory.Exists(_root.Combine("box")));
    }

    [Fact]
    public void The_worker_reports_invalid_configuration_in_plain_words()
    {
        var result = RunApp([], ("AgentBridge__PollSeconds", "0"), ("AgentBridge__LogRetentionDays", "0"));

        Assert.True(result.ExitCode == 1, result.Describe());
        Assert.Contains("Configuration is invalid:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("AgentBridge:PollSeconds must be between 1 and 3600.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("AgentBridge:LogRetentionDays must be between 1 and 3650.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", result.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_root.Combine("data")));
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("run")]
    public void Local_commands_log_to_stderr_only(string command)
    {
        var result = command == "run" ? RunApp(["run", "agent.ping"]) : RunApp(["validate"]);

        Assert.True(result.ExitCode == 0, result.Describe());
        Assert.False(Directory.Exists(LogFolder));
    }

    /// <summary>
    /// The app as built in its own project folder, like scripts/selftest.ps1 runs it. The test output folder of the net10.0
    /// run does not hold every assembly the app needs, because the .NET 10 runtime provides some of them.
    /// </summary>
    private static string AppDll { get; } = FindAppDll();

    private static string FindAppDll()
    {
        var configuration = Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)))!;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "AgentBridge.sln")))
            {
                return Path.Combine(folder.FullName, "src", "AgentBridge", "bin", configuration, "net8.0", "AgentBridge.dll");
            }
        }
        throw new InvalidOperationException($"AgentBridge.sln was not found above {AppContext.BaseDirectory}.");
    }

    private static List<string> ReadLines(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>Runs the built AgentBridge.dll as its own process, the way a user starts it.</summary>
    private AppResult RunApp(string[] args, params (string Name, string Value)[] environment)
    {
        var info = new ProcessStartInfo(TestSupport.TestTools.DotnetPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _root.Path,
        };
        // Same runtime as this test run: the net10.0 run must start the app on .NET 10, not roll forward to an installed .NET 8.
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add("--fx-version");
        info.ArgumentList.Add(Environment.Version.ToString());
        info.ArgumentList.Add(AppDll);
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        info.Environment["DOTNET_ENVIRONMENT"] = "Production";
        info.Environment["AgentBridge__BoxRoot"] = _root.Combine("box");
        info.Environment["AgentBridge__LocalDataRoot"] = _root.Combine("data");
        foreach (var (name, value) in environment)
        {
            info.Environment[name] = value;
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("AgentBridge did not exit within 60 seconds.");
        }
        return new AppResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private sealed record AppResult(int ExitCode, string Stdout, string Stderr)
    {
        public string Describe() => $"exit code {ExitCode}{Environment.NewLine}stdout:{Environment.NewLine}{Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{Stderr}";
    }
}
