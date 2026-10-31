using System.Diagnostics.CodeAnalysis;

namespace AgentBridge.Capabilities;

/// <summary>
/// Enabled capabilities with configuration overrides applied. Built once at startup; invalid configuration
/// (duplicate names, missing descriptions, unknown types) fails construction with a clear message.
/// </summary>
public interface ICapabilityRegistry
{
    /// <summary>Enabled capabilities, ordered by name.</summary>
    IReadOnlyList<ICapability> All { get; }

    /// <summary>Case-insensitive lookup. Disabled capabilities are not found.</summary>
    bool TryGet(string name, [NotNullWhen(true)] out ICapability? capability);
}
