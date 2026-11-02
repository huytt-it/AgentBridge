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
    public void Without_box_root_uses_box_drive_when_it_exists()
    {
        var boxDrive = _root.Combine("Box");
        Directory.CreateDirectory(boxDrive);
        var options = TestEnvironment.CreateOptions(_root);
        options.BoxRoot = "";

        Assert.True(new AgentBridgeOptionsValidator(boxDrive).Validate(null, options).Succeeded);
    }

    [Fact]
    public void Without_box_root_fails_when_box_drive_is_missing()
    {
        var boxDrive = _root.Combine("Box");
        var options = TestEnvironment.CreateOptions(_root);
        options.BoxRoot = "";

        var result = new AgentBridgeOptionsValidator(boxDrive).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            $"BoxRoot is not set and Box Drive was not found at {boxDrive}; install and sign in to Box Drive, or set BoxRoot in appsettings.Local.json.",
            Assert.Single(result.Failures!),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_configured_box_root_does_not_need_box_drive()
    {
        var options = TestEnvironment.CreateOptions(_root);

        Assert.True(new AgentBridgeOptionsValidator(_root.Combine("no-box-drive")).Validate(null, options).Succeeded);
    }

    [Fact]
    public void Rejects_local_data_root_inside_the_default_box_root()
    {
        var boxDrive = _root.Combine("Box");
        Directory.CreateDirectory(boxDrive);
        var options = TestEnvironment.CreateOptions(_root);
        options.BoxRoot = "";
        options.LocalDataRoot = Path.Combine(boxDrive, "AgentBridge", "data");

        var result = new AgentBridgeOptionsValidator(boxDrive).Validate(null, options);

        Assert.Contains(result.Failures!, f => f.Contains("LocalDataRoot", StringComparison.Ordinal));
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
