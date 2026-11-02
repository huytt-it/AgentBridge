using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Contracts;
using Microsoft.Extensions.Options;

namespace AgentBridge.Capabilities;

internal sealed class CatalogProvider : ICatalogProvider
{
    private const string TargetPlaceholder = "<target name>";
    private const string RequestIdPlaceholder = "<new unique id, e.g. a GUID>";
    private const int VersionLength = 12;

    // Indented output would use the OS line ending, and the version must be the same on every machine.
    private static readonly JsonSerializerOptions HashOptions = new(JsonDefaults.Options) { WriteIndented = false };

    public CatalogProvider(ICapabilityRegistry registry, IOptions<AgentBridgeOptions> options, TimeProvider timeProvider)
    {
        var targets = options.Value.Targets
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => new CatalogTarget(t.Key, t.Value.Description))
            .ToList();
        var capabilities = registry.All
            .Select(c => Describe(c.Descriptor, targets.Count > 0 ? targets[0].Name : TargetPlaceholder))
            .ToList();

        Current = new Catalog
        {
            CatalogVersion = ComputeVersion(targets, registry.Domains, registry.Playbooks, capabilities),
            GeneratedAtUtc = timeProvider.GetUtcNow(),
            Agent = new AgentInfo(options.Value.AgentId, AgentVersion.Current),
            Targets = targets,
            Domains = registry.Domains,
            Playbooks = registry.Playbooks,
            Capabilities = capabilities,
        };
    }

    public Catalog Current { get; }

    public string RenderReadme() => ReadmeRenderer.Render(Current);

    private static CatalogCapability Describe(CapabilityDescriptor descriptor, string exampleTarget)
    {
        var example = new JsonObject
        {
            ["schemaVersion"] = AgentRequest.CurrentSchemaVersion,
            ["requestId"] = RequestIdPlaceholder,
            ["capability"] = descriptor.Name,
        };
        if (descriptor.RequiresTarget)
        {
            example["target"] = exampleTarget;
        }
        // Cloned: a node can have only one parent, and the descriptor's node must stay reusable.
        example["args"] = descriptor.ExampleArgs?.DeepClone() ?? new JsonObject();

        return new CatalogCapability
        {
            Name = descriptor.Name,
            Domain = Naming.DomainOf(descriptor.Name),
            Description = descriptor.Description,
            UseWhen = descriptor.UseWhen,
            ExampleQuestions = descriptor.ExampleQuestions,
            Access = descriptor.Access,
            RequiresTarget = descriptor.RequiresTarget,
            Args = new SortedDictionary<string, ParameterDescriptor>(
                descriptor.Parameters.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal),
            Output = descriptor.Output,
            TimeoutSeconds = (int)Math.Ceiling(descriptor.Timeout.TotalSeconds),
            Example = example,
        };
    }

    /// <summary>Hash of everything the cloud may act on. The agent version and generation time are left out on purpose.</summary>
    private static string ComputeVersion(
        IReadOnlyList<CatalogTarget> targets,
        IReadOnlyList<CatalogDomain> domains,
        IReadOnlyList<CatalogPlaybook> playbooks,
        IReadOnlyList<CatalogCapability> capabilities)
    {
        var canonical = new
        {
            SchemaVersion = Catalog.CurrentSchemaVersion,
            Targets = targets,
            Domains = domains,
            Playbooks = playbooks,
            Capabilities = capabilities,
        };
        var hash = Hashing.Sha256Hex(JsonSerializer.SerializeToUtf8Bytes(canonical, HashOptions));
        return hash[..VersionLength];
    }
}
