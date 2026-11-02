using System.Diagnostics.CodeAnalysis;
using AgentBridge.Contracts;

namespace AgentBridge.Capabilities;

/// <summary>
/// Enabled capabilities with configuration overrides applied, grouped into business domains, and the playbooks that
/// use them. Built once at startup; invalid configuration (duplicate names, missing descriptions, unknown types,
/// playbook steps that name no capability) fails construction with a clear message.
/// </summary>
public interface ICapabilityRegistry
{
    /// <summary>Enabled capabilities, ordered by name.</summary>
    IReadOnlyList<ICapability> All { get; }

    /// <summary>Domains that have at least one enabled capability, ordered by name, as published in catalog.json.</summary>
    IReadOnlyList<CatalogDomain> Domains { get; }

    /// <summary>Enabled playbooks whose steps all name enabled capabilities, ordered by name, as published in catalog.json.</summary>
    IReadOnlyList<CatalogPlaybook> Playbooks { get; }

    /// <summary>Valid configuration with an effect worth knowing, e.g. a playbook left out of the catalog. validate prints them.</summary>
    IReadOnlyList<string> Notes { get; }

    /// <summary>Case-insensitive lookup. Disabled capabilities are not found.</summary>
    bool TryGet(string name, [NotNullWhen(true)] out ICapability? capability);
}
