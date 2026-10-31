using AgentBridge.Capabilities;
using AgentBridge.Cli;
using AgentBridge.Contracts;
using AgentBridge.Tests.TestSupport;
using Tools = AgentBridge.Tests.TestSupport.TestTools;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentBridge.Tests.Cli;

internal static class CliTestSupport
{
    public const string Name = "test.tool";

    /// <summary>The config key prefix every factory error starts with.</summary>
    public const string Key = "AgentBridge:Capabilities:test.tool";

    public static ProcessRunner Runner { get; } = new(NullLogger<ProcessRunner>.Instance);

    public static CliCapabilityFactory Factory(string? searchPath = null) =>
        new(Runner, NullLoggerFactory.Instance) { SearchPath = searchPath };

    /// <summary>A cli entry that runs the stand-in tool: <paramref name="toolArgs"/> are the templates after "cli".</summary>
    public static CapabilityOptions Entry(string output, params string[] toolArgs) => new()
    {
        Type = "cli",
        Exe = Tools.DotnetPath,
        Description = "Test tool.",
        Output = output,
        Args = Tools.Args(["cli", .. toolArgs]),
    };

    public static ParameterOptions Param(string type = "string", bool required = false, string? pattern = null, string? example = null) =>
        new() { Type = type, Required = required, Description = "A test param.", Pattern = pattern, Example = example };

    public static (ICapability? Capability, List<string> Errors) Create(CapabilityOptions options, string? searchPath = null)
    {
        var errors = new List<string>();
        var capability = Factory(searchPath).Create(Name, options, errors);
        return (capability, errors);
    }

    public static ICapability CreateValid(CapabilityOptions options)
    {
        var (capability, errors) = Create(options);
        Assert.Empty(errors);
        return Assert.IsAssignableFrom<ICapability>(capability);
    }

    /// <summary>A job folder inside <paramref name="root"/>: logs go to job/, artifacts to job/artifacts/.</summary>
    public static CapabilityContext Context(TempDirectory root, string argsJson = "{}", string? targetPath = null)
    {
        var jobDirectory = root.Combine("job");
        Directory.CreateDirectory(jobDirectory);
        using var document = System.Text.Json.JsonDocument.Parse(argsJson);
        return new CapabilityContext
        {
            Request = new AgentRequest { SchemaVersion = 1, RequestId = "r1", Capability = Name },
            Args = document.RootElement.Clone(),
            TargetName = targetPath is null ? null : "project",
            TargetPath = targetPath,
            JobDirectory = jobDirectory,
            ArtifactDirectory = Path.Combine(jobDirectory, "artifacts"),
        };
    }

    /// <summary>Reads a log the tool may still be writing.</summary>
    public static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Overall guard so a broken test fails instead of hanging.</summary>
    public static CancellationToken Guard() => new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token;
}
