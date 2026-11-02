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

    /// <param name="boxDriveRoot">Plays Box Drive's folder, so no test depends on whether this machine has Box Drive.</param>
    public static AgentBridgePaths CreatePaths(AgentBridgeOptions options, string? boxDriveRoot = null) =>
        new(options, Environment.CurrentDirectory, boxDriveRoot ?? Path.Combine(Path.GetTempPath(), "agentbridge-tests", "no-box-drive"));
}
