namespace AgentBridge.Tests.TestSupport;

/// <summary>Options and paths rooted in a temp folder: box/ plays the Box root, data/ the local data root.</summary>
public static class TestEnvironment
{
    public static AgentBridgeOptions CreateOptions(TempDirectory root) => new()
    {
        AgentId = "test-agent",
        BoxRoot = root.Combine("box"),
        LocalDataRoot = root.Combine("data"),
        PollSeconds = 1,
        IncompleteFileGraceSeconds = 60,
    };

    public static AgentBridgePaths CreatePaths(AgentBridgeOptions options) => new(options, Environment.CurrentDirectory);
}
