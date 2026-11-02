using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;

namespace AgentBridge.Tests.Processing.Fakes;

internal sealed class FakeCapability : ICapability
{
    private readonly Func<CapabilityContext, CancellationToken, Task<CapabilityResult>> _execute;
    private int _executions;

    public FakeCapability(CapabilityDescriptor descriptor, Func<CapabilityContext, CancellationToken, Task<CapabilityResult>>? execute = null)
    {
        Descriptor = descriptor;
        _execute = execute ?? ((_, _) => Task.FromResult(CapabilityResult.Success(new JsonObject { ["ok"] = true }, "done")));
    }

    public CapabilityDescriptor Descriptor { get; }
    public int Executions => Volatile.Read(ref _executions);
    public CapabilityContext? LastContext { get; private set; }

    /// <summary>Completes when ExecuteAsync is first called.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<CapabilityResult> ExecuteAsync(CapabilityContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _executions);
        LastContext = context;
        Started.TrySetResult();
        return await _execute(context, cancellationToken);
    }
}

internal sealed class FakeCapabilityRegistry : ICapabilityRegistry
{
    public FakeCapabilityRegistry(IEnumerable<ICapability> capabilities)
    {
        All = capabilities.OrderBy(c => c.Descriptor.Name, StringComparer.Ordinal).ToList();
        Domains = All
            .GroupBy(c => Naming.DomainOf(c.Descriptor.Name))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CatalogDomain { Name = g.Key, Capabilities = g.Select(c => c.Descriptor.Name).ToList() })
            .ToList();
    }

    public IReadOnlyList<ICapability> All { get; }

    /// <summary>One domain per name prefix, without texts, unless a test sets its own.</summary>
    public IReadOnlyList<CatalogDomain> Domains { get; set; }

    public IReadOnlyList<CatalogPlaybook> Playbooks { get; set; } = [];

    public IReadOnlyList<string> Notes { get; set; } = [];

    public bool TryGet(string name, [NotNullWhen(true)] out ICapability? capability)
    {
        capability = All.FirstOrDefault(c => string.Equals(c.Descriptor.Name, name, StringComparison.OrdinalIgnoreCase));
        return capability is not null;
    }
}

internal sealed class FakeCatalogProvider : ICatalogProvider
{
    public const string Version = "catalog-v1";

    public FakeCatalogProvider(ICapabilityRegistry registry, AgentBridgeOptions options) => Current = new Catalog
    {
        CatalogVersion = Version,
        Agent = new AgentInfo(options.AgentId, AgentVersion.Current),
        Targets = options.Targets.Select(t => new CatalogTarget(t.Key, t.Value.Description)).ToList(),
        Capabilities = registry.All
            .Select(c => new CatalogCapability
            {
                Name = c.Descriptor.Name,
                Description = c.Descriptor.Description,
                RequiresTarget = c.Descriptor.RequiresTarget,
                Args = c.Descriptor.Parameters,
                TimeoutSeconds = (int)c.Descriptor.Timeout.TotalSeconds,
            })
            .ToList(),
    };

    public Catalog Current { get; }

    public string RenderReadme() => "# AgentBridge test readme";
}
