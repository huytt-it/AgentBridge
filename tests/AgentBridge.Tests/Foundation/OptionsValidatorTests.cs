using AgentBridge.Tests.TestSupport;

namespace AgentBridge.Tests.Foundation;

public sealed class OptionsValidatorTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly AgentBridgeOptionsValidator _validator = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Accepts_a_complete_configuration()
    {
        var options = TestEnvironment.CreateOptions(_root);
        options.Targets["DemoRepo"] = new TargetOptions { Path = _root.Path, Description = "Demo repository" };

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Requires_box_root()
    {
        var options = TestEnvironment.CreateOptions(_root);
        options.BoxRoot = "";

        AssertFailsWith(options, "BoxRoot");
    }

    [Fact]
    public void Rejects_local_data_root_inside_box_root()
    {
        var options = TestEnvironment.CreateOptions(_root);
        options.LocalDataRoot = Path.Combine(options.BoxRoot, "data");

        AssertFailsWith(options, "LocalDataRoot");
    }

    [Fact]
    public void Rejects_targets_that_do_not_exist()
    {
        var options = TestEnvironment.CreateOptions(_root);
        options.Targets["Missing"] = new TargetOptions { Path = _root.Combine("nope"), Description = "x" };

        AssertFailsWith(options, "does not exist");
    }

    [Fact]
    public void Rejects_relative_target_paths()
    {
        var options = TestEnvironment.CreateOptions(_root);
        options.Targets["Relative"] = new TargetOptions { Path = "some/dir", Description = "x" };

        AssertFailsWith(options, "absolute path");
    }

    [Fact]
    public void Requires_target_descriptions()
    {
        var options = TestEnvironment.CreateOptions(_root);
        options.Targets["DemoRepo"] = new TargetOptions { Path = _root.Path };

        AssertFailsWith(options, "Description is required");
    }

    [Fact]
    public void Rejects_invalid_capability_names()
    {
        var options = TestEnvironment.CreateOptions(_root);
        options.Capabilities["Ping"] = new CapabilityOptions();

        AssertFailsWith(options, "Capability name 'Ping'");
    }

    private void AssertFailsWith(AgentBridgeOptions options, string expectedFragment)
    {
        var result = _validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains(expectedFragment, StringComparison.Ordinal));
    }
}
