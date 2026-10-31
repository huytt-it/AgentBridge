using AgentBridge.Contracts;

namespace AgentBridge.Capabilities;

public interface ICatalogProvider
{
    /// <summary>
    /// Catalog built once from the registry and the configured targets. CatalogVersion is a content hash,
    /// identical across restarts while nothing the cloud may ask for has changed.
    /// </summary>
    Catalog Current { get; }

    /// <summary>README.md for the cloud: how to write a request, where to read the response, what statuses mean.</summary>
    string RenderReadme();
}
