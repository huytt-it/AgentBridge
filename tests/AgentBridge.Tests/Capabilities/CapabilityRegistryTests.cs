using AgentBridge.Capabilities;
using AgentBridge.Capabilities.Native;
using AgentBridge.Contracts;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static AgentBridge.Tests.Capabilities.CapabilityTestSupport;

namespace AgentBridge.Tests.Capabilities;

public sealed class CapabilityRegistryTests
{
    [Fact]
    public void Native_capabilities_are_enabled_without_configuration()
    {
        var ping = new PingCapability(CreateOptions(), new FakeTimeProvider());

        var registry = CreateRegistry(ping);

        Assert.Same(ping, Assert.Single(registry.All));
        Assert.True(registry.TryGet("agent.ping", out var found));
        Assert.Same(ping, found);
    }

    [Fact]
    public void Disabled_capability_is_excluded()
    {
        var options = CreateOptions(o => o.Capabilities["test.fake"] = new CapabilityOptions { Enabled = false });

        var registry = CreateRegistry(options, new FakeCapability("test.fake"), new FakeCapability("test.other"));

        Assert.Equal(["test.other"], registry.All.Select(c => c.Descriptor.Name));
        Assert.False(registry.TryGet("test.fake", out var found));
        Assert.Null(found);
    }

    [Fact]
    public void Config_entry_without_overrides_keeps_the_capability_as_is()
    {
        var fake = new FakeCapability();
        var options = CreateOptions(o => o.Capabilities["test.fake"] = new CapabilityOptions { Type = "native" });

        var registry = CreateRegistry(options, fake);

        Assert.Same(fake, Assert.Single(registry.All));
    }

    [Fact]
    public async Task Config_overrides_description_use_when_and_timeout()
    {
        var fake = new FakeCapability(new CapabilityDescriptor
        {
            Name = "test.fake",
            Description = "Built-in description.",
            UseWhen = "Built-in hint.",
            RequiresTarget = true,
            Output = OutputFormats.Markdown,
            Timeout = TimeSpan.FromSeconds(30),
        });
        var options = CreateOptions(o => o.Capabilities["test.fake"] = new CapabilityOptions
        {
            Description = "Configured description.",
            UseWhen = "Configured hint.",
            TimeoutSeconds = 120,
        });

        var registry = CreateRegistry(options, fake);

        var capability = Assert.Single(registry.All);
        Assert.Equal("Configured description.", capability.Descriptor.Description);
        Assert.Equal("Configured hint.", capability.Descriptor.UseWhen);
        Assert.Equal(TimeSpan.FromSeconds(120), capability.Descriptor.Timeout);
        Assert.True(capability.Descriptor.RequiresTarget);
        Assert.Equal(OutputFormats.Markdown, capability.Descriptor.Output);

        await capability.ExecuteAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(1, fake.ExecuteCount);
    }

    [Fact]
    public void Partial_overrides_keep_the_other_built_in_values()
    {
        var fake = new FakeCapability(new CapabilityDescriptor
        {
            Name = "test.fake",
            Description = "Built-in description.",
            UseWhen = "Built-in hint.",
            Timeout = TimeSpan.FromSeconds(30),
        });
        var options = CreateOptions(o => o.Capabilities["test.fake"] = new CapabilityOptions { TimeoutSeconds = 5 });

        var descriptor = Assert.Single(CreateRegistry(options, fake).All).Descriptor;

        Assert.Equal("Built-in description.", descriptor.Description);
        Assert.Equal("Built-in hint.", descriptor.UseWhen);
        Assert.Equal(TimeSpan.FromSeconds(5), descriptor.Timeout);
    }

    [Fact]
    public void Blank_use_when_override_clears_the_hint()
    {
        var fake = new FakeCapability(new CapabilityDescriptor { Name = "test.fake", Description = "d", UseWhen = "hint" });
        var options = CreateOptions(o => o.Capabilities["test.fake"] = new CapabilityOptions { UseWhen = " " });

        Assert.Null(Assert.Single(CreateRegistry(options, fake).All).Descriptor.UseWhen);
    }

    [Theory]
    [InlineData("agent.ping")]
    [InlineData("AGENT.PING")]
    [InlineData("Agent.Ping")]
    public void Lookup_is_case_insensitive(string name)
    {
        var registry = CreateRegistry(new FakeCapability("agent.ping"));

        Assert.True(registry.TryGet(name, out var found));
        Assert.Equal("agent.ping", found!.Descriptor.Name);
    }

    [Fact]
    public void Unknown_names_are_not_found()
    {
        var registry = CreateRegistry(new FakeCapability("agent.ping"));

        Assert.False(registry.TryGet("agent.pong", out _));
        Assert.False(registry.TryGet("", out _));
    }

    [Fact]
    public void All_is_ordered_by_name()
    {
        var registry = CreateRegistry(
            new FakeCapability("code.map"), new FakeCapability("agent.ping"), new FakeCapability("backlog.export"), new FakeCapability("agent.info"));

        Assert.Equal(
            ["agent.info", "agent.ping", "backlog.export", "code.map"],
            registry.All.Select(c => c.Descriptor.Name));
    }

    [Theory]
    [InlineData("Ping")]
    [InlineData("agent")]
    [InlineData("agent.Ping")]
    [InlineData("agent ping")]
    [InlineData("agent..ping")]
    [InlineData("")]
    public void Rejects_invalid_capability_names(string name)
    {
        var failures = AssertFails(() => CreateRegistry(new FakeCapability(name)));

        Assert.Contains(failures, f => f.Contains($"Capability name '{name}' is invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_invalid_names_in_configuration()
    {
        var options = CreateOptions(o => o.Capabilities["Ping"] = new CapabilityOptions());

        var failures = AssertFails(() => CreateRegistry(options, new FakeCapability("agent.ping")));

        Assert.Contains(failures, f => f.Contains("Capability name 'Ping' is invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_duplicate_names()
    {
        var failures = AssertFails(() => CreateRegistry(new FakeCapability("agent.ping"), new FakeCapability("agent.ping")));

        Assert.Contains(failures, f => f.Contains("'agent.ping' is registered more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_duplicate_names_that_differ_only_in_case()
    {
        var failures = AssertFails(() => CreateRegistry(new FakeCapability("agent.ping"), new FakeCapability("Agent.Ping")));

        Assert.Contains(failures, f => f.Contains("more than once", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_an_empty_built_in_description(string description)
    {
        var fake = new FakeCapability(new CapabilityDescriptor { Name = "test.fake", Description = description });

        var failures = AssertFails(() => CreateRegistry(fake));

        Assert.Contains(failures, f => f.StartsWith("AgentBridge:Capabilities:test.fake:Description", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_an_empty_description_override()
    {
        var options = CreateOptions(o => o.Capabilities["test.fake"] = new CapabilityOptions { Description = " " });

        var failures = AssertFails(() => CreateRegistry(options, new FakeCapability()));

        Assert.Contains(failures, f => f.StartsWith("AgentBridge:Capabilities:test.fake:Description", StringComparison.Ordinal));
    }

    [Fact]
    public void A_description_override_repairs_an_empty_built_in_description()
    {
        var fake = new FakeCapability(new CapabilityDescriptor { Name = "test.fake", Description = "" });
        var options = CreateOptions(o => o.Capabilities["test.fake"] = new CapabilityOptions { Description = "Now described." });

        Assert.Equal("Now described.", Assert.Single(CreateRegistry(options, fake).All).Descriptor.Description);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Rejects_a_built_in_timeout_of_zero_or_less(int seconds)
    {
        var fake = new FakeCapability(new CapabilityDescriptor
        {
            Name = "test.fake", Description = "d", Timeout = TimeSpan.FromSeconds(seconds),
        });

        var failures = AssertFails(() => CreateRegistry(fake));

        Assert.Contains(failures, f => f.StartsWith("AgentBridge:Capabilities:test.fake:TimeoutSeconds", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_timeout_override_of_zero_or_less(int seconds)
    {
        var options = CreateOptions(o => o.Capabilities["test.fake"] = new CapabilityOptions { TimeoutSeconds = seconds });

        var failures = AssertFails(() => CreateRegistry(options, new FakeCapability()));

        Assert.Contains(failures, f => f.StartsWith("AgentBridge:Capabilities:test.fake:TimeoutSeconds", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Echo")]
    [InlineData("my-arg")]
    [InlineData("1st")]
    [InlineData("_hidden")]
    [InlineData("")]
    public void Rejects_parameter_names_that_are_not_camel_or_snake_case(string parameterName)
    {
        var fake = WithParameter(parameterName, new ParameterDescriptor());

        var failures = AssertFails(() => CreateRegistry(fake));

        Assert.Contains(failures, f => f.Contains($"parameter '{parameterName}' must be camelCase or snake_case", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("echo")]
    [InlineData("maxItems")]
    [InlineData("top10")]
    [InlineData("file_path")]
    public void Accepts_camel_and_snake_case_parameter_names(string parameterName)
    {
        Assert.Single(CreateRegistry(WithParameter(parameterName, new ParameterDescriptor())).All);
    }

    [Theory]
    [InlineData("String")]
    [InlineData("int")]
    [InlineData("")]
    public void Rejects_unknown_parameter_types(string type)
    {
        var fake = WithParameter("count", new ParameterDescriptor { Type = type });

        var failures = AssertFails(() => CreateRegistry(fake));

        Assert.Contains(failures, f => f.Contains($"parameter 'count' has unknown type '{type}'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ParameterTypes.String)]
    [InlineData(ParameterTypes.Integer)]
    [InlineData(ParameterTypes.Number)]
    [InlineData(ParameterTypes.Boolean)]
    [InlineData(ParameterTypes.Object)]
    [InlineData(ParameterTypes.Array)]
    public void Accepts_every_declared_parameter_type(string type)
    {
        Assert.Single(CreateRegistry(WithParameter("value", new ParameterDescriptor { Type = type })).All);
    }

    [Fact]
    public void Rejects_a_pattern_that_does_not_compile()
    {
        var fake = WithParameter("text", new ParameterDescriptor { Pattern = "([unclosed" });

        var failures = AssertFails(() => CreateRegistry(fake));

        Assert.Contains(failures, f => f.Contains("parameter 'text' has a Pattern that is not a valid regular expression", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_entry_for_an_unregistered_native_capability_is_an_error()
    {
        var options = CreateOptions(o => o.Capabilities["backlog.export"] = new CapabilityOptions { Type = "native" });

        var failures = AssertFails(() => CreateRegistry(options, new FakeCapability("agent.ping")));

        var failure = Assert.Single(failures);
        Assert.StartsWith("AgentBridge:Capabilities:backlog.export:Type", failure, StringComparison.Ordinal);
        Assert.Contains("no native capability named 'backlog.export'", failure, StringComparison.Ordinal);
        Assert.Contains("agent.ping", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Type_defaults_to_native_when_blank()
    {
        var options = CreateOptions(o => o.Capabilities["test.fake"] = new CapabilityOptions { Type = "" });

        Assert.Single(CreateRegistry(options, new FakeCapability()).All);
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("mcp")]
    public void Types_without_a_factory_are_rejected(string type)
    {
        var options = CreateOptions(o => o.Capabilities["backlog.export"] = new CapabilityOptions { Type = type });

        var failures = AssertFails(() => CreateRegistry(options, new FakeCapability("agent.ping")));

        var failure = Assert.Single(failures);
        Assert.StartsWith("AgentBridge:Capabilities:backlog.export:Type", failure, StringComparison.Ordinal);
        Assert.Contains($"'{type}' is unknown; supported types are native", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Factories_build_config_driven_capabilities()
    {
        var factory = new FakeCapabilityFactory();
        var options = CreateOptions(o => o.Capabilities["backlog.export"] = new CapabilityOptions { Type = "FAKE", Description = "Exports the backlog." });

        var registry = CreateRegistry(options, [factory], new FakeCapability("agent.ping"));

        Assert.Equal(["agent.ping", "backlog.export"], registry.All.Select(c => c.Descriptor.Name));
        Assert.Equal(["backlog.export"], factory.Created);
    }

    [Fact]
    public void Disabled_config_driven_entries_are_never_created()
    {
        var factory = new FakeCapabilityFactory();
        var options = CreateOptions(o => o.Capabilities["backlog.export"] = new CapabilityOptions { Type = "fake", Enabled = false, Description = "x" });

        var registry = CreateRegistry(options, [factory]);

        Assert.Empty(registry.All);
        Assert.Empty(factory.Created);
    }

    [Fact]
    public void Factory_errors_are_reported()
    {
        var factory = new FakeCapabilityFactory { ErrorToReport = "was not found." };
        var options = CreateOptions(o => o.Capabilities["backlog.export"] = new CapabilityOptions { Type = "fake", Description = "x" });

        var failures = AssertFails(() => CreateRegistry(options, [factory]));

        Assert.Equal("AgentBridge:Capabilities:backlog.export:Exe was not found.", Assert.Single(failures));
    }

    [Fact]
    public void Config_driven_capabilities_are_validated_like_native_ones()
    {
        var options = CreateOptions(o => o.Capabilities["backlog.export"] = new CapabilityOptions { Type = "fake", Description = " " });

        var failures = AssertFails(() => CreateRegistry(options, [new FakeCapabilityFactory()]));

        Assert.Contains(failures, f => f.StartsWith("AgentBridge:Capabilities:backlog.export:Description", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_driven_capabilities_cannot_reuse_a_built_in_name()
    {
        var options = CreateOptions(o => o.Capabilities["agent.ping"] = new CapabilityOptions { Type = "fake", Description = "x" });

        var failures = AssertFails(() => CreateRegistry(options, [new FakeCapabilityFactory()], new FakeCapability("agent.ping")));

        Assert.Contains(failures, f => f.Contains("is the name of a built-in capability", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_unknown_types()
    {
        var options = CreateOptions(o => o.Capabilities["agent.ping"] = new CapabilityOptions { Type = "powershell" });

        var failures = AssertFails(() => CreateRegistry(options, new FakeCapability("agent.ping")));

        var failure = Assert.Single(failures);
        Assert.StartsWith("AgentBridge:Capabilities:agent.ping:Type", failure, StringComparison.Ordinal);
        Assert.Contains("'powershell' is unknown", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_every_problem_at_once()
    {
        var options = CreateOptions(o =>
        {
            o.Capabilities["test.fake"] = new CapabilityOptions { TimeoutSeconds = 0 };
            o.Capabilities["test.missing"] = new CapabilityOptions();
            o.Capabilities["test.remote"] = new CapabilityOptions { Type = "mcp" };
        });
        var broken = new FakeCapability(new CapabilityDescriptor
        {
            Name = "test.fake",
            Description = "",
            Parameters = new Dictionary<string, ParameterDescriptor> { ["Bad"] = new() { Type = "text", Pattern = "(" } },
        });

        var exception = Assert.Throws<OptionsValidationException>(() => CreateRegistry(options, broken, new FakeCapability("Bad.Name")));

        Assert.Equal(AgentBridgeOptions.SectionName, exception.OptionsName);
        Assert.Equal(typeof(AgentBridgeOptions), exception.OptionsType);
        Assert.Equal(8, exception.Failures.Count());
    }

    private static FakeCapability WithParameter(string name, ParameterDescriptor parameter) =>
        new(new CapabilityDescriptor
        {
            Name = "test.fake",
            Description = "d",
            Parameters = new Dictionary<string, ParameterDescriptor> { [name] = parameter },
        });

    private static List<string> AssertFails(Action build) =>
        Assert.Throws<OptionsValidationException>(build).Failures.ToList();
}
