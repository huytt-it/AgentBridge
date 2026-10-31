using System.Text.Json.Nodes;
using AgentBridge.Box;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;
using AgentBridge.Jobs;
using AgentBridge.Processing;
using AgentBridge.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AgentBridge.Tests.Processing.Fakes;

/// <summary>A RequestProcessor wired to fakes, a temp folder and a fake clock. One per test.</summary>
internal sealed class PipelineHarness : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    public PipelineHarness(IEnumerable<ICapability> capabilities, Action<AgentBridgeOptions>? configure = null)
    {
        Options = TestEnvironment.CreateOptions(Root);
        configure?.Invoke(Options);
        Paths = TestEnvironment.CreatePaths(Options);
        Jobs = new FakeJobStore(Paths.Jobs, Time);
        Registry = new FakeCapabilityRegistry(capabilities);
        Catalog = new FakeCatalogProvider(Registry, Options);
        Processor = new RequestProcessor(Box, Jobs, Registry, Catalog, MsOptions.Create(Options), Time, Logger);
    }

    public PipelineHarness(params ICapability[] capabilities)
        : this((IEnumerable<ICapability>)capabilities)
    {
    }

    public TempDirectory Root { get; } = new();
    public FakeTimeProvider Time { get; } = new(Start);
    public AgentBridgeOptions Options { get; }
    public AgentBridgePaths Paths { get; }
    public FakeBoxFolder Box { get; } = new();
    public FakeJobStore Jobs { get; }
    public FakeCapabilityRegistry Registry { get; }
    public FakeCatalogProvider Catalog { get; }
    public ListLogger<RequestProcessor> Logger { get; } = new();
    public ListLogger<InboxWorker> WorkerLogger { get; } = new();
    public RequestProcessor Processor { get; }

    public static FakeCapability Ping(
        Func<CapabilityContext, CancellationToken, Task<CapabilityResult>>? execute = null, TimeSpan? timeout = null) =>
        new(new CapabilityDescriptor
        {
            Name = "agent.ping",
            Description = "Checks that the agent is reachable.",
            Parameters = new Dictionary<string, ParameterDescriptor>
            {
                ["echo"] = new() { Type = ParameterTypes.String, Description = "Text to send back." },
            },
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        }, execute);

    public static string RequestJson(string requestId, string capability = "agent.ping", Action<JsonObject>? configure = null)
    {
        var json = new JsonObject { ["schemaVersion"] = 1, ["requestId"] = requestId, ["capability"] = capability };
        configure?.Invoke(json);
        return json.ToJsonString();
    }

    public InboxItem AddRequest(string requestId, Action<JsonObject>? configure = null) =>
        Box.AddRequest($"{requestId}.json", RequestJson(requestId, configure: configure), Time.GetUtcNow());

    public Task ProcessInboxAsync() => Processor.ProcessInboxAsync(CancellationToken.None);

    public InboxWorker CreateWorker() =>
        new(Processor, Box, Jobs, Catalog, Paths, MsOptions.Create(Options), Time, WorkerLogger);

    public ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(MsOptions.Create(Options));
        services.AddSingleton(Paths);
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IBoxFolder>(Box);
        services.AddSingleton<IJobStore>(Jobs);
        services.AddSingleton<ICapabilityRegistry>(Registry);
        services.AddSingleton<ICatalogProvider>(Catalog);
        services.AddSingleton<RequestProcessor>();
        return services.BuildServiceProvider();
    }

    public void Dispose() => Root.Dispose();
}
