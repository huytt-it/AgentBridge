namespace AgentBridge.Capabilities;

/// <summary>A capability whose description, usage hint or timeout is replaced from configuration.</summary>
internal sealed class OverriddenCapability : ICapability
{
    private readonly ICapability _inner;

    public OverriddenCapability(ICapability inner, CapabilityDescriptor descriptor)
    {
        _inner = inner;
        Descriptor = descriptor;
    }

    public CapabilityDescriptor Descriptor { get; }

    public Task<CapabilityResult> ExecuteAsync(CapabilityContext context, CancellationToken cancellationToken) =>
        _inner.ExecuteAsync(context, cancellationToken);
}
