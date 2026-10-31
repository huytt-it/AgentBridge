using AgentBridge;
using AgentBridge.Hosting;
using AgentBridge.Mcp;
using AgentBridge.Processing;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

const string Usage = """
    AgentBridge - local capability runtime driven by a Box-synced inbox.

    Usage:
      AgentBridge                      Run the worker (polls the inbox until stopped).
      AgentBridge validate             Check configuration and print the catalog.
      AgentBridge run <capability> [--target <name>] [--args <json>]
                                       Execute one capability locally, without Box.
      AgentBridge mcp-tools [<server>] [--json]
                                       List the tools of the configured MCP servers, with entries to paste.

    Machine-specific settings go in appsettings.Local.json next to appsettings.json (git ignores it).
    """;

if (args is ["help"] or ["-h"] or ["--help"])
{
    Console.WriteLine(Usage);
    return 0;
}

var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "worker";
var isWorker = command == "worker";
string[] commandArgs = isWorker ? [] : args[1..];

// Config sits next to the executable, so it is found whether the worker is started by dotnet run,
// by double-click or by a service manager.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = isWorker ? args : [],
    ContentRootPath = AppContext.BaseDirectory,
});

// Machine-specific settings (BoxRoot, targets, tools, secrets) live in appsettings.Local.json, which git ignores.
// Environment variables and the command line are added again so they still win over that file.
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(isWorker ? args : []);

builder.Services.AddAgentBridge(builder.Configuration);
if (isWorker)
{
    builder.Services.AddHostedService<InboxWorker>();
}
else
{
    // validate and run print their result on stdout; logs go to stderr so that output stays clean JSON.
    builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
}

using var host = builder.Build();

try
{
    switch (command)
    {
        case "worker":
            return await RunWorkerAsync(host);
        case "validate":
            return await LocalCommands.ValidateAsync(host.Services, commandArgs, Console.Out, CancellationToken.None);
        case "run":
            return await LocalCommands.RunAsync(host.Services, commandArgs, Console.Out, CancellationToken.None);
        case "mcp-tools":
            return await McpCommands.ListToolsAsync(host.Services, commandArgs, Console.Out, CancellationToken.None);
        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (OptionsValidationException ex)
{
    Console.Error.WriteLine("Configuration is invalid:");
    foreach (var failure in ex.Failures)
    {
        Console.Error.WriteLine($"  - {failure}");
    }
    return 1;
}

static async Task<int> RunWorkerAsync(IHost host)
{
    var paths = host.Services.GetRequiredService<AgentBridgePaths>();
    using var instanceLock = InstanceLock.TryAcquire(paths.LockFile);
    if (instanceLock is null)
    {
        Console.Error.WriteLine($"Another AgentBridge worker is already running (lock: {paths.LockFile}).");
        return 1;
    }

    try
    {
        await host.RunAsync();
    }
    catch (Exception ex) when (ex is not OptionsValidationException)
    {
        Console.Error.WriteLine($"AgentBridge stopped because of an error: {ex.Message}");
        return 1;
    }

    // A worker that fails after startup stops the host instead of throwing; report it through the exit code.
    var worker = host.Services.GetServices<IHostedService>().OfType<InboxWorker>().Single();
    return worker.ExecuteTask is { IsFaulted: true } ? 1 : 0;
}
