namespace AgentBridge.Capabilities;

/// <summary>
/// Builds the config-driven capabilities of one <see cref="CapabilityOptions.Type"/> ("cli", "mcp").
/// The registry calls it once per enabled entry while it is built at startup.
/// </summary>
public interface ICapabilityFactory
{
    /// <summary>The lowercase Type value this factory handles.</summary>
    string Type { get; }

    /// <summary>
    /// Creates the capability for one config entry, whose descriptor must carry <paramref name="name"/>.
    /// Problems are added to <paramref name="errors"/>, each naming its config key
    /// (e.g. "AgentBridge:Capabilities:backlog.export:Exe ..."); return null when the entry cannot be used.
    /// </summary>
    ICapability? Create(string name, CapabilityOptions options, IList<string> errors);
}
