namespace AgentBridge.Tests.Foundation;

public class AgentBridgePathsTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "agentbridge-base");

    [Fact]
    public void Resolves_relative_paths_against_the_base_directory()
    {
        var paths = new AgentBridgePaths(new AgentBridgeOptions { BoxRoot = "box/AgentBridge", LocalDataRoot = "data" }, Base);

        Assert.Equal(Path.Combine(Base, "box", "AgentBridge"), paths.BoxRoot);
        Assert.Equal(Path.Combine(paths.BoxRoot, "inbox"), paths.Inbox);
        Assert.Equal(Path.Combine(paths.BoxRoot, "dead-letter"), paths.DeadLetter);
        Assert.Equal(Path.Combine(Base, "data", "jobs"), paths.Jobs);
    }

    [Fact]
    public void Defaults_the_local_data_root_to_local_app_data()
    {
        var paths = new AgentBridgePaths(new AgentBridgeOptions { BoxRoot = "box" }, Base);

        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridge");
        Assert.Equal(expected, paths.LocalDataRoot);
    }

    [Theory]
    [InlineData("box", "box", true)]
    [InlineData("box/data", "box", true)]
    [InlineData("data", "box", false)]
    [InlineData("box-other", "box", false)]
    [InlineData("..foo", ".", true)]
    public void Detects_paths_inside_a_root(string path, string root, bool expected)
    {
        Assert.Equal(expected, AgentBridgePaths.IsSameOrInside(Path.Combine(Base, path), Path.GetFullPath(Path.Combine(Base, root))));
    }
}
