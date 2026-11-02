using AgentBridge.Capabilities;
using AgentBridge.Contracts;
using Microsoft.Extensions.Options;
using static AgentBridge.Tests.Capabilities.CapabilityTestSupport;

namespace AgentBridge.Tests.Capabilities;

public sealed class DomainsAndPlaybooksTests
{
    private const string WriteRefused =
        "Access 'write' needs a local approval step, which this version does not have; only 'read' capabilities can be enabled.";

    // ---- Domains ----

    [Fact]
    public void Capabilities_are_grouped_by_the_first_segment_of_their_names_without_any_configuration()
    {
        var registry = CreateRegistry(new FakeCapability("code.search"), new FakeCapability("backlog.items"), new FakeCapability("backlog.export"));

        Assert.Equal(["backlog", "code"], registry.Domains.Select(d => d.Name));
        var backlog = registry.Domains[0];
        Assert.Equal(["backlog.export", "backlog.items"], backlog.Capabilities);
        Assert.Null(backlog.Description);
        Assert.Null(backlog.UseWhen);
        Assert.Empty(backlog.ExampleQuestions);
    }

    [Fact]
    public void Domains_and_their_capabilities_are_sorted_by_name()
    {
        var registry = CreateRegistry(
            new FakeCapability("code.search"), new FakeCapability("code-review.open"), new FakeCapability("agent.ping"), new FakeCapability("code.map"));

        Assert.Equal(["agent", "code", "code-review"], registry.Domains.Select(d => d.Name));
        Assert.Equal(["code.map", "code.search"], registry.Domains[1].Capabilities);
    }

    [Fact]
    public void Configured_domain_texts_are_published()
    {
        var options = CreateOptions(o => o.Domains["backlog"] = new DomainOptions
        {
            Description = "Work items of the projects.",
            UseWhen = "The user asks about scope or progress.",
            ExampleQuestions = ["  What is still open in project DEMO? ", " ", "Who works on DEMO?"],
        });

        var domain = Assert.Single(CreateRegistry(options, new FakeCapability("backlog.export")).Domains);

        Assert.Equal("Work items of the projects.", domain.Description);
        Assert.Equal("The user asks about scope or progress.", domain.UseWhen);
        Assert.Equal(["What is still open in project DEMO?", "Who works on DEMO?"], domain.ExampleQuestions);
    }

    [Fact]
    public void The_agent_domain_has_a_built_in_description_unless_configured()
    {
        var builtIn = Assert.Single(CreateRegistry(new FakeCapability("agent.ping")).Domains);
        var configured = Assert.Single(CreateRegistry(
            CreateOptions(o => o.Domains["agent"] = new DomainOptions { Description = "The bridge itself." }),
            new FakeCapability("agent.ping")).Domains);

        Assert.Equal("agent", builtIn.Name);
        Assert.Contains("reachable", builtIn.Description, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(builtIn.UseWhen));
        Assert.NotEmpty(builtIn.ExampleQuestions);
        Assert.Equal("The bridge itself.", configured.Description);
        Assert.Equal(builtIn.UseWhen, configured.UseWhen);
    }

    [Fact]
    public void A_disabled_domain_disables_its_native_capabilities()
    {
        var options = CreateOptions(o => o.Domains["backlog"] = new DomainOptions { Enabled = false });

        var registry = CreateRegistry(options, new FakeCapability("backlog.export"), new FakeCapability("code.search"));

        Assert.Equal(["code.search"], registry.All.Select(c => c.Descriptor.Name));
        Assert.False(registry.TryGet("backlog.export", out _));
        Assert.Equal(["code"], registry.Domains.Select(d => d.Name));
    }

    [Fact]
    public void A_disabled_domain_never_creates_its_config_driven_capabilities()
    {
        var factory = new FakeCapabilityFactory();
        var options = CreateOptions(o =>
        {
            o.Domains["code"] = new DomainOptions { Enabled = false };
            o.Capabilities["code.search"] = new CapabilityOptions { Type = "fake", Description = "Searches." };
            o.Capabilities["backlog.export"] = new CapabilityOptions { Type = "fake", Description = "Exports." };
        });

        var registry = CreateRegistry(options, [factory]);

        Assert.Equal(["backlog.export"], factory.Created);
        Assert.Equal(["backlog.export"], registry.All.Select(c => c.Descriptor.Name));
    }

    [Fact]
    public void Domains_without_enabled_capabilities_are_left_out()
    {
        var options = CreateOptions(o =>
        {
            o.Domains["backlog"] = new DomainOptions { Description = "Work items." };
            o.Capabilities["backlog.export"] = new CapabilityOptions { Enabled = false };
        });

        var registry = CreateRegistry(options, new FakeCapability("backlog.export"), new FakeCapability("agent.ping"));

        Assert.Equal(["agent"], registry.Domains.Select(d => d.Name));
        Assert.Empty(registry.Notes);
    }

    [Fact]
    public void A_configured_domain_that_no_capability_belongs_to_is_a_note()
    {
        var options = CreateOptions(o => o.Domains["backlgo"] = new DomainOptions { Description = "Typo." });

        var registry = CreateRegistry(options, new FakeCapability("backlog.export"));

        Assert.Equal(["backlog"], registry.Domains.Select(d => d.Name));
        Assert.Contains("Domain 'backlgo' is configured, but no capability name starts with 'backlgo.'", Assert.Single(registry.Notes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Backlog")]
    [InlineData("backlog.export")]
    [InlineData("back_log")]
    [InlineData("1code")]
    public void Rejects_invalid_domain_names(string name)
    {
        var options = CreateOptions(o => o.Domains[name] = new DomainOptions());

        var failure = Assert.Single(AssertFails(() => CreateRegistry(options, new FakeCapability("agent.ping"))));

        Assert.StartsWith($"Domain name '{name}' is invalid", failure, StringComparison.Ordinal);
    }

    // ---- Access ----

    [Fact]
    public void Access_defaults_to_read()
    {
        var options = CreateOptions(o => o.Capabilities["backlog.export"] = new CapabilityOptions { Type = "fake", Description = "Exports." });

        var registry = CreateRegistry(options, [new FakeCapabilityFactory()], new FakeCapability("agent.ping"));

        Assert.All(registry.All, c => Assert.Equal(AccessLevels.Read, c.Descriptor.Access));
    }

    [Fact]
    public void Read_access_can_be_set_on_capabilities_and_domains_in_any_case()
    {
        var fake = new FakeCapability("backlog.export");
        var options = CreateOptions(o =>
        {
            o.Domains["code"] = new DomainOptions { Access = "Read" };
            o.Capabilities["backlog.export"] = new CapabilityOptions { Access = " READ " };
            o.Capabilities["code.search"] = new CapabilityOptions { Type = "fake", Description = "Searches." };
        });

        var registry = CreateRegistry(options, [new FakeCapabilityFactory()], fake);

        Assert.All(registry.All, c => Assert.Equal(AccessLevels.Read, c.Descriptor.Access));
        Assert.Same(fake, registry.All[0]);
    }

    public static TheoryData<Action<AgentBridgeOptions>, string> WriteAccessSources => new()
    {
        { o => o.Capabilities["backlog.export"] = new CapabilityOptions { Type = "fake", Description = "x", Access = "write" }, "AgentBridge:Capabilities:backlog.export:" },
        { o => o.Capabilities["agent.ping"] = new CapabilityOptions { Access = "write" }, "AgentBridge:Capabilities:agent.ping:" },
        { o => o.Domains["agent"] = new DomainOptions { Access = "write" }, "AgentBridge:Domains:agent:" },
    };

    [Theory]
    [MemberData(nameof(WriteAccessSources))]
    public void Write_access_cannot_be_enabled(Action<AgentBridgeOptions> configure, string key)
    {
        var failures = AssertFails(() => CreateRegistry(CreateOptions(configure), [new FakeCapabilityFactory()], new FakeCapability("agent.ping")));

        Assert.Equal(key + WriteRefused, Assert.Single(failures));
    }

    [Fact]
    public void A_native_capability_that_declares_write_access_cannot_be_enabled_even_if_configuration_says_read()
    {
        var writer = new FakeCapability(new CapabilityDescriptor { Name = "backlog.close", Description = "Closes items.", Access = AccessLevels.Write });
        var options = CreateOptions(o => o.Capabilities["backlog.close"] = new CapabilityOptions { Access = "read" });

        var failure = Assert.Single(AssertFails(() => CreateRegistry(options, writer)));

        Assert.StartsWith("Capability 'backlog.close': its built-in Access", failure, StringComparison.Ordinal);
        Assert.Contains(WriteRefused, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_access_is_accepted_on_disabled_capabilities_and_domains()
    {
        var factory = new FakeCapabilityFactory();
        var options = CreateOptions(o =>
        {
            o.Capabilities["backlog.close"] = new CapabilityOptions { Type = "fake", Description = "x", Access = "write", Enabled = false };
            o.Domains["deploy"] = new DomainOptions { Access = "write", Enabled = false };
            o.Capabilities["deploy.release"] = new CapabilityOptions { Type = "fake", Description = "x" };
        });

        var registry = CreateRegistry(options, [factory], new FakeCapability("agent.ping"));

        Assert.Equal(["agent.ping"], registry.All.Select(c => c.Descriptor.Name));
        Assert.Empty(factory.Created);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unknown_access_level_is_its_own_error_even_when_disabled(bool enabled)
    {
        var options = CreateOptions(o => o.Capabilities["agent.ping"] = new CapabilityOptions { Access = "admin", Enabled = enabled });

        var failure = Assert.Single(AssertFails(() => CreateRegistry(options, new FakeCapability("agent.ping"))));

        Assert.StartsWith("AgentBridge:Capabilities:agent.ping:Access 'admin' is unknown", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("approval", failure, StringComparison.Ordinal);
    }

    // ---- Example questions ----

    [Fact]
    public void Configured_example_questions_replace_the_built_in_ones_for_every_type()
    {
        var native = new FakeCapability(new CapabilityDescriptor { Name = "agent.ping", Description = "d", ExampleQuestions = ["Built in?"] });
        var options = CreateOptions(o =>
        {
            o.Capabilities["agent.ping"] = new CapabilityOptions { ExampleQuestions = ["Is the machine on?", ""] };
            o.Capabilities["backlog.export"] = new CapabilityOptions { Type = "fake", Description = "x", ExampleQuestions = ["What is open in DEMO?"] };
        });

        var registry = CreateRegistry(options, [new FakeCapabilityFactory()], native);

        Assert.Equal(["Is the machine on?"], registry.All[0].Descriptor.ExampleQuestions);
        Assert.Equal(["What is open in DEMO?"], registry.All[1].Descriptor.ExampleQuestions);
    }

    [Fact]
    public void Built_in_example_questions_stay_without_configuration()
    {
        var native = new FakeCapability(new CapabilityDescriptor { Name = "agent.ping", Description = "d", ExampleQuestions = ["Built in?"] });

        var registry = CreateRegistry(CreateOptions(o => o.Capabilities["agent.ping"] = new CapabilityOptions()), native);

        Assert.Same(native, Assert.Single(registry.All));
        Assert.Equal(["Built in?"], native.Descriptor.ExampleQuestions);
    }

    // ---- Playbooks ----

    private static PlaybookOptions Playbook(params string[] capabilities) => new()
    {
        Goal = "Find where the open items of a project touch the code.",
        UseWhen = "The user asks what a backlog means for the code.",
        ExampleQuestions = ["Which code do the open DEMO items affect?"],
        Steps = capabilities.Select(c => new PlaybookStepOptions { Capability = c, Purpose = $"Purpose of {c}." }).ToList(),
    };

    private static ICapability[] BacklogAndCode() => [new FakeCapability("backlog.export"), new FakeCapability("code.search")];

    [Fact]
    public void A_playbook_is_published_with_its_steps_in_the_configured_order()
    {
        var options = CreateOptions(o => o.Playbooks["backlog-impact"] = Playbook("code.search", "Backlog.Export"));

        var playbook = Assert.Single(CreateRegistry(options, BacklogAndCode()).Playbooks);

        Assert.Equal("backlog-impact", playbook.Name);
        Assert.Equal("Find where the open items of a project touch the code.", playbook.Goal);
        Assert.Equal("The user asks what a backlog means for the code.", playbook.UseWhen);
        Assert.Equal(["Which code do the open DEMO items affect?"], playbook.ExampleQuestions);
        Assert.Equal(
            [new CatalogPlaybookStep("code.search", "Purpose of code.search."), new CatalogPlaybookStep("backlog.export", "Purpose of Backlog.Export.")],
            playbook.Steps);
    }

    [Fact]
    public void Playbooks_are_sorted_by_name()
    {
        var options = CreateOptions(o =>
        {
            o.Playbooks["zeta"] = Playbook("code.search");
            o.Playbooks["alpha"] = Playbook("backlog.export");
        });

        Assert.Equal(["alpha", "zeta"], CreateRegistry(options, BacklogAndCode()).Playbooks.Select(p => p.Name));
    }

    [Fact]
    public void Disabled_playbooks_are_left_out_and_not_checked()
    {
        var options = CreateOptions(o => o.Playbooks["draft"] = new PlaybookOptions { Enabled = false, Steps = [new() { Capability = "nothing.here" }] });

        var registry = CreateRegistry(options, BacklogAndCode());

        Assert.Empty(registry.Playbooks);
        Assert.Empty(registry.Notes);
    }

    [Fact]
    public void A_step_naming_a_capability_that_does_not_exist_is_an_error_naming_the_playbook_and_the_step()
    {
        var options = CreateOptions(o => o.Playbooks["backlog-impact"] = Playbook("backlog.export", "code.serch"));

        var failure = Assert.Single(AssertFails(() => CreateRegistry(options, BacklogAndCode())));

        Assert.StartsWith("AgentBridge:Playbooks:backlog-impact:Steps:1:Capability 'code.serch' (step 2 of playbook 'backlog-impact')", failure, StringComparison.Ordinal);
        Assert.Contains("backlog.export, code.search", failure, StringComparison.Ordinal);
    }

    public static TheoryData<string, Action<AgentBridgeOptions>, string> DisabledStepCapabilities => new()
    {
        { "capability", o => o.Capabilities["code.search"] = new CapabilityOptions { Enabled = false }, "is disabled (AgentBridge:Capabilities:code.search:Enabled is false)" },
        { "domain", o => o.Domains["code"] = new DomainOptions { Enabled = false }, "belongs to the disabled domain 'code' (AgentBridge:Domains:code:Enabled is false)" },
    };

    [Theory]
    [MemberData(nameof(DisabledStepCapabilities))]
    public void A_playbook_whose_step_capability_is_disabled_is_left_out_with_a_note(string through, Action<AgentBridgeOptions> disable, string reason)
    {
        var options = CreateOptions(o =>
        {
            o.Playbooks["backlog-impact"] = Playbook("backlog.export", "code.search");
            disable(o);
        });

        var registry = CreateRegistry(options, BacklogAndCode());

        Assert.Empty(registry.Playbooks);
        Assert.Equal(
            $"Playbook 'backlog-impact' is left out of the catalog: step 2 uses 'code.search', which {reason}.",
            Assert.Single(registry.Notes));
        Assert.True(registry.TryGet("backlog.export", out _), through);
    }

    [Fact]
    public void A_step_may_use_a_disabled_config_driven_capability_without_creating_it()
    {
        var factory = new FakeCapabilityFactory();
        var options = CreateOptions(o =>
        {
            o.Capabilities["code.search"] = new CapabilityOptions { Type = "fake", Description = "x", Enabled = false };
            o.Playbooks["backlog-impact"] = Playbook("backlog.export", "code.search");
        });

        var registry = CreateRegistry(options, [factory], new FakeCapability("backlog.export"));

        Assert.Empty(registry.Playbooks);
        Assert.Single(registry.Notes);
        Assert.Empty(factory.Created);
    }

    [Fact]
    public void A_playbook_needs_a_goal_steps_and_a_purpose_and_capability_for_every_step()
    {
        var options = CreateOptions(o =>
        {
            o.Playbooks["no-goal"] = new PlaybookOptions { Goal = " ", Steps = [new() { Capability = "code.search", Purpose = "p" }] };
            o.Playbooks["no-steps"] = new PlaybookOptions { Goal = "g" };
            o.Playbooks["bad-step"] = new PlaybookOptions { Goal = "g", Steps = [new() { Capability = "code.search" }, new() { Purpose = "p" }] };
        });

        var failures = AssertFails(() => CreateRegistry(options, BacklogAndCode()));

        Assert.Equal(4, failures.Count);
        Assert.Contains(failures, f => f.StartsWith("AgentBridge:Playbooks:bad-step:Steps:0:Purpose is required", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("AgentBridge:Playbooks:bad-step:Steps:1:Capability is required", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("AgentBridge:Playbooks:no-goal:Goal is required", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("AgentBridge:Playbooks:no-steps:Steps needs at least one step", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Backlog-Impact")]
    [InlineData("backlog_impact")]
    [InlineData("backlog.impact")]
    [InlineData("1st")]
    public void Rejects_invalid_playbook_names(string name)
    {
        var options = CreateOptions(o => o.Playbooks[name] = Playbook("code.search"));

        var failure = Assert.Single(AssertFails(() => CreateRegistry(options, BacklogAndCode())));

        Assert.StartsWith($"Playbook name '{name}' is invalid", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Domain_playbook_and_capability_problems_are_reported_together()
    {
        var options = CreateOptions(o =>
        {
            o.Domains["Code"] = new DomainOptions();
            o.Capabilities["backlog.export"] = new CapabilityOptions { Access = "write" };
            o.Playbooks["backlog-impact"] = Playbook("code.nothing");
            o.Capabilities["test.missing"] = new CapabilityOptions();
        });

        var exception = Assert.Throws<OptionsValidationException>(() => CreateRegistry(options, BacklogAndCode()));

        Assert.Equal(AgentBridgeOptions.SectionName, exception.OptionsName);
        Assert.Equal(4, exception.Failures.Count());
    }

    private static List<string> AssertFails(Action build) =>
        Assert.Throws<OptionsValidationException>(build).Failures.ToList();
}
