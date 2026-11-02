using System.Globalization;
using System.Text.Json;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;
using Microsoft.Extensions.Options;

namespace AgentBridge.Processing;

/// <summary>Commands for checking the setup locally. They never read or write the Box folder.</summary>
internal static class LocalCommands
{
    private const string ValidateUsage = "Usage: AgentBridge validate [--json]";

    private const string RunUsage = """
        Usage: AgentBridge run <capability> [--target <name>] [--args <json object>]
        Example: AgentBridge run agent.ping --args '{"echo":"hi"}'
        """;

    /// <summary>"validate": checks configuration, builds the registry and catalog, prints the catalog. Returns the exit code.</summary>
    public static Task<int> ValidateAsync(IServiceProvider services, string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        var json = false;
        foreach (var arg in args)
        {
            if (arg != "--json")
            {
                output.WriteLine($"Unknown option '{arg}'.");
                output.WriteLine(ValidateUsage);
                return Task.FromResult(2);
            }
            json = true;
        }

        var options = services.GetRequiredService<IOptions<AgentBridgeOptions>>().Value;
        var paths = services.GetRequiredService<AgentBridgePaths>();
        var registry = services.GetRequiredService<ICapabilityRegistry>();
        var catalog = services.GetRequiredService<ICatalogProvider>().Current;

        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(catalog, JsonDefaults.Options));
            return Task.FromResult(0);
        }

        var targets = options.Targets.Count == 0
            ? "(none)"
            : string.Join(", ", options.Targets.Keys.Order(StringComparer.OrdinalIgnoreCase));
        var servers = options.McpServers.Count == 0
            ? "(none)"
            : string.Join(", ", options.McpServers.Where(s => s.Value.Enabled).Select(s => s.Key).Order(StringComparer.OrdinalIgnoreCase));
        var boxNotes = new List<string>();
        if (paths.BoxRootIsDefault)
        {
            boxNotes.Add("Box Drive default, because BoxRoot is not set");
        }
        if (!Directory.Exists(paths.BoxRoot))
        {
            boxNotes.Add("missing: created at first start; it must be inside the Box Drive folder");
        }
        var boxState = boxNotes.Count == 0 ? "" : $"  ({string.Join("; ", boxNotes)})";
        output.WriteLine("Configuration is valid.");
        output.WriteLine($"  BoxRoot:        {paths.BoxRoot}{boxState}");
        output.WriteLine($"  LocalDataRoot:  {paths.LocalDataRoot}");
        output.WriteLine($"  Targets:        {targets}");
        output.WriteLine($"  MCP servers:    {servers}");
        output.WriteLine($"  CatalogVersion: {catalog.CatalogVersion}");
        output.WriteLine($"Capabilities ({registry.All.Count}):");
        foreach (var capability in registry.All)
        {
            output.WriteLine($"  {capability.Descriptor.Name} - {capability.Descriptor.Description}");
        }
        return Task.FromResult(0);
    }

    /// <summary>"run &lt;capability&gt; [--target name] [--args json]": executes one capability without Box and prints the response.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        if (!TryParseRunArgs(args, out var capability, out var target, out var requestArgs, out var usageError))
        {
            await output.WriteLineAsync(usageError);
            await output.WriteLineAsync(RunUsage);
            return 2;
        }

        var paths = services.GetRequiredService<AgentBridgePaths>();
        var time = services.GetRequiredService<TimeProvider>();
        var processor = services.GetRequiredService<RequestProcessor>();

        var stamp = time.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var request = new AgentRequest
        {
            SchemaVersion = AgentRequest.CurrentSchemaVersion,
            RequestId = $"local-{stamp}-{Guid.NewGuid().ToString("N")[..8]}",
            Capability = capability,
            Target = target,
            Args = requestArgs,
            Purpose = "Local run from the command line.",
        };

        // Never under jobs/, so a local run cannot affect dedup or recovery.
        var workDirectory = Path.Combine(paths.LocalDataRoot, "local-runs", request.RequestId);
        Directory.CreateDirectory(workDirectory);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onCancelKey = (_, e) =>
        {
            // The first Ctrl+C stops the capability; a second one ends the process.
            if (!cancellation.IsCancellationRequested)
            {
                e.Cancel = true;
                cancellation.Cancel();
            }
        };
        Console.CancelKeyPress += onCancelKey;
        try
        {
            var response = await processor.ExecuteAsync(request, workDirectory, cancellation.Token);
            await output.WriteLineAsync(JsonSerializer.Serialize(response, JsonDefaults.Options));

            // A local run publishes nothing; point at the files so they can be checked. stderr keeps stdout pure JSON.
            var artifactDirectory = RequestProcessor.ArtifactDirectoryOf(workDirectory);
            if (Directory.Exists(artifactDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(artifactDirectory, "*", SearchOption.AllDirectories))
                {
                    Console.Error.WriteLine($"Artifact (local only, not published): {file}");
                }
            }
            return response.Status == JobStatus.Succeeded ? 0 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancelKey;
        }
    }

    private static bool TryParseRunArgs(
        string[] args, out string capability, out string? target, out JsonElement? requestArgs, out string error)
    {
        capability = "";
        target = null;
        requestArgs = null;
        error = "";
        string? argsJson = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--target" or "--args")
            {
                if (i + 1 >= args.Length)
                {
                    error = $"{arg} needs a value.";
                    return false;
                }
                if ((arg == "--target" ? target : argsJson) is not null)
                {
                    error = $"{arg} is given more than once.";
                    return false;
                }
                if (arg == "--target")
                {
                    target = args[++i];
                }
                else
                {
                    argsJson = args[++i];
                }
            }
            else if (arg.StartsWith('-'))
            {
                error = $"Unknown option '{arg}'.";
                return false;
            }
            else if (capability.Length > 0)
            {
                error = $"Unexpected argument '{arg}'.";
                return false;
            }
            else
            {
                capability = arg;
            }
        }

        if (capability.Length == 0)
        {
            error = "Name the capability to run.";
            return false;
        }
        if (argsJson is not null)
        {
            try
            {
                requestArgs = JsonSerializer.Deserialize<JsonElement>(argsJson, JsonDefaults.Options);
            }
            catch (JsonException)
            {
                error = "--args must be a JSON object, for example '{\"echo\":\"hi\"}'.";
                return false;
            }
        }
        return true;
    }
}
