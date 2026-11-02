using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Capabilities;
using AgentBridge.Capabilities.Native;
using AgentBridge.Contracts;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static AgentBridge.Tests.Capabilities.CapabilityTestSupport;

namespace AgentBridge.Tests.Capabilities;

public sealed class CatalogProviderTests
{
    private const string SecretPath = @"D:\Work\SECRET-CLIENT-FOLDER\DemoRepo";

    private static readonly DateTimeOffset Now = new(2026, 10, 31, 8, 30, 0, TimeSpan.Zero);

    private static CatalogProvider Build(Action<AgentBridgeOptions>? configure = null, DateTimeOffset? at = null, params ICapability[] capabilities)
    {
        var options = CreateOptions(configure);
        capabilities = capabilities.Length > 0 ? capabilities : [new PingCapability(options, new FakeTimeProvider(Now))];
        return new CatalogProvider(CreateRegistry(options, capabilities), options, new FakeTimeProvider(at ?? Now));
    }

    private static void AddDemoRepo(AgentBridgeOptions options) =>
        options.Targets["DemoRepo"] = new TargetOptions { Path = SecretPath, Description = "Demo repository" };

    private static FakeCapability Fake(Func<CapabilityDescriptor, CapabilityDescriptor>? customize = null)
    {
        var descriptor = new CapabilityDescriptor { Name = "test.fake", Description = "Fake description." };
        return new FakeCapability(customize?.Invoke(descriptor) ?? descriptor);
    }

    [Fact]
    public void Describes_the_agent_and_stamps_the_generation_time()
    {
        var catalog = Build(at: Now.AddHours(2)).Current;

        Assert.Equal(Catalog.CurrentSchemaVersion, catalog.SchemaVersion);
        Assert.Equal(new AgentInfo("test-agent", AgentVersion.Current), catalog.Agent);
        Assert.Equal(Now.AddHours(2), catalog.GeneratedAtUtc);
    }

    [Fact]
    public void Lists_capabilities_from_the_registry()
    {
        var catalog = Build().Current;

        var ping = Assert.Single(catalog.Capabilities);
        Assert.Equal("agent.ping", ping.Name);
        Assert.Contains("agent id", ping.Description, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(ping.UseWhen));
        Assert.False(ping.RequiresTarget);
        Assert.Equal(OutputFormats.Json, ping.Output);
        Assert.Equal(10, ping.TimeoutSeconds);
        Assert.Equal(ParameterTypes.String, ping.Args["echo"].Type);
    }

    [Fact]
    public void Uses_configured_overrides_not_the_built_in_values()
    {
        var provider = Build(o => o.Capabilities["agent.ping"] = new CapabilityOptions
        {
            Description = "Configured.", UseWhen = "Configured hint.", TimeoutSeconds = 45,
        });

        var ping = Assert.Single(provider.Current.Capabilities);
        Assert.Equal("Configured.", ping.Description);
        Assert.Equal("Configured hint.", ping.UseWhen);
        Assert.Equal(45, ping.TimeoutSeconds);
    }

    [Fact]
    public void Disabled_capabilities_are_not_listed()
    {
        var provider = Build(o => o.Capabilities["agent.ping"] = new CapabilityOptions { Enabled = false }, capabilities: [Fake(), new FakeCapability("agent.ping")]);

        Assert.Equal(["test.fake"], provider.Current.Capabilities.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Targets_are_sorted_and_carry_only_name_and_description()
    {
        var provider = Build(o =>
        {
            o.Targets["Zeta"] = new TargetOptions { Path = SecretPath, Description = "Last" };
            AddDemoRepo(o);
        });

        Assert.Equal(
            [new CatalogTarget("DemoRepo", "Demo repository"), new CatalogTarget("Zeta", "Last")],
            provider.Current.Targets.ToArray());
    }

    [Fact]
    public void Args_are_sorted_by_name()
    {
        var fake = Fake(d => d with
        {
            Parameters = new Dictionary<string, ParameterDescriptor>
            {
                ["zebra"] = new(), ["apple"] = new(), ["mango"] = new(),
            },
        });

        var args = Assert.Single(Build(capabilities: [fake]).Current.Capabilities).Args;

        Assert.Equal(["apple", "mango", "zebra"], args.Keys.ToArray());
    }

    [Fact]
    public void Local_paths_never_appear_in_the_catalog_or_the_readme()
    {
        var provider = Build(AddDemoRepo);

        var catalogJson = JsonSerializer.Serialize(provider.Current, JsonDefaults.Options);
        var readme = provider.RenderReadme();

        foreach (var text in new[] { catalogJson, readme })
        {
            Assert.DoesNotContain("SECRET-CLIENT-FOLDER", text, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretPath, text, StringComparison.Ordinal);
            Assert.DoesNotContain(JsonSerializer.Serialize(SecretPath), text, StringComparison.Ordinal);
        }
        Assert.Contains("Demo repository", catalogJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_is_twelve_lowercase_hex_characters()
    {
        Assert.Matches("^[0-9a-f]{12}$", Build().Current.CatalogVersion);
    }

    [Fact]
    public void Version_is_stable_across_builds_at_different_times()
    {
        var first = Build(AddDemoRepo, Now).Current;
        var second = Build(AddDemoRepo, Now.AddDays(30)).Current;

        Assert.NotEqual(first.GeneratedAtUtc, second.GeneratedAtUtc);
        Assert.Equal(first.CatalogVersion, second.CatalogVersion);
    }

    [Fact]
    public void Version_ignores_the_agent_id_and_target_paths()
    {
        var first = Build(o => { AddDemoRepo(o); o.AgentId = "laptop-a"; }).Current;
        var second = Build(o =>
        {
            AddDemoRepo(o);
            o.Targets["DemoRepo"].Path = @"E:\Elsewhere\DemoRepo";
            o.AgentId = "laptop-b";
        }).Current;

        Assert.Equal(first.CatalogVersion, second.CatalogVersion);
    }

    [Fact]
    public void Version_does_not_depend_on_the_order_targets_were_configured()
    {
        var first = Build(o =>
        {
            o.Targets["Alpha"] = new TargetOptions { Path = SecretPath, Description = "a" };
            o.Targets["Beta"] = new TargetOptions { Path = SecretPath, Description = "b" };
        }).Current;
        var second = Build(o =>
        {
            o.Targets["Beta"] = new TargetOptions { Path = SecretPath, Description = "b" };
            o.Targets["Alpha"] = new TargetOptions { Path = SecretPath, Description = "a" };
        }).Current;

        Assert.Equal(first.CatalogVersion, second.CatalogVersion);
    }

    [Fact]
    public void Version_does_not_depend_on_the_order_args_were_declared()
    {
        static CapabilityDescriptor Declare(CapabilityDescriptor d, params string[] names) => d with
        {
            Parameters = names.ToDictionary(n => n, n => new ParameterDescriptor { Description = n }),
        };

        var first = Build(capabilities: [Fake(d => Declare(d, "alpha", "beta"))]).Current;
        var second = Build(capabilities: [Fake(d => Declare(d, "beta", "alpha"))]).Current;

        Assert.Equal(first.CatalogVersion, second.CatalogVersion);
    }

    /// <summary>Applied on top of a configuration that already has the DemoRepo target.</summary>
    public static TheoryData<string, Action<AgentBridgeOptions>> ChangesTheCloudCanSee => new()
    {
        { "description", o => o.Capabilities["test.fake"] = new CapabilityOptions { Description = "Another description." } },
        { "useWhen", o => o.Capabilities["test.fake"] = new CapabilityOptions { UseWhen = "Another hint." } },
        { "timeout", o => o.Capabilities["test.fake"] = new CapabilityOptions { TimeoutSeconds = 99 } },
        { "target added", o => o.Targets["Other"] = new TargetOptions { Path = SecretPath, Description = "Another target" } },
        { "target removed", o => o.Targets.Remove("DemoRepo") },
        { "target description", o => o.Targets["DemoRepo"].Description = "Changed" },
        { "target renamed", o => { o.Targets.Remove("DemoRepo"); o.Targets["DemoRepo2"] = new TargetOptions { Path = SecretPath, Description = "Demo repository" }; } },
        { "capability example questions", o => o.Capabilities["test.fake"] = new CapabilityOptions { ExampleQuestions = ["Is it there?"] } },
        { "domain description", o => o.Domains["test"] = new DomainOptions { Description = "Test things." } },
        { "domain useWhen", o => o.Domains["test"] = new DomainOptions { UseWhen = "Always." } },
        { "domain example questions", o => o.Domains["test"] = new DomainOptions { ExampleQuestions = ["What is tested?"] } },
        { "playbook added", o => o.Playbooks["check"] = Playbook("test.fake") },
    };

    private static PlaybookOptions Playbook(params string[] capabilities) => new()
    {
        Goal = "Check the test things.",
        Steps = capabilities.Select(c => new PlaybookStepOptions { Capability = c, Purpose = $"Run {c}." }).ToList(),
    };

    /// <summary>Applied to the "check" playbook of a configuration with test.fake and test.other.</summary>
    public static TheoryData<string, Action<PlaybookOptions>> PlaybookChanges => new()
    {
        { "goal", p => p.Goal = "Another goal." },
        { "useWhen", p => p.UseWhen = "When asked." },
        { "example questions", p => p.ExampleQuestions = ["Is everything checked?"] },
        { "step purpose", p => p.Steps[0].Purpose = "Another purpose." },
        { "step order", p => p.Steps.Reverse() },
        { "step added", p => p.Steps.Add(new PlaybookStepOptions { Capability = "test.fake", Purpose = "Again." }) },
        { "disabled", p => p.Enabled = false },
    };

    [Theory]
    [MemberData(nameof(PlaybookChanges))]
    public void Version_changes_when_a_playbook_changes(string change, Action<PlaybookOptions> configure)
    {
        ICapability[] capabilities = [Fake(), new FakeCapability("test.other")];
        var baseline = Build(o => o.Playbooks["check"] = Playbook("test.fake", "test.other"), capabilities: capabilities).Current.CatalogVersion;

        var changed = Build(o =>
        {
            o.Playbooks["check"] = Playbook("test.fake", "test.other");
            configure(o.Playbooks["check"]);
        }, capabilities: capabilities).Current.CatalogVersion;

        Assert.True(baseline != changed, $"The catalog version should change when the playbook's {change} changes");
    }

    [Fact]
    public void Version_does_not_depend_on_the_order_domains_and_playbooks_were_configured()
    {
        ICapability[] capabilities = [Fake(), new FakeCapability("code.search")];
        var first = Build(o =>
        {
            o.Domains["test"] = new DomainOptions { Description = "t" };
            o.Domains["code"] = new DomainOptions { Description = "c" };
            o.Playbooks["beta"] = Playbook("code.search");
            o.Playbooks["alpha"] = Playbook("test.fake");
        }, capabilities: capabilities).Current;
        var second = Build(o =>
        {
            o.Playbooks["alpha"] = Playbook("test.fake");
            o.Playbooks["beta"] = Playbook("code.search");
            o.Domains["code"] = new DomainOptions { Description = "c" };
            o.Domains["test"] = new DomainOptions { Description = "t" };
        }, capabilities: capabilities).Current;

        Assert.Equal(first.CatalogVersion, second.CatalogVersion);
        Assert.Equal(["code", "test"], first.Domains.Select(d => d.Name));
        Assert.Equal(["alpha", "beta"], first.Playbooks.Select(p => p.Name));
    }

    [Fact]
    public void Capabilities_name_their_domain_access_and_example_questions()
    {
        var provider = Build(o => o.Capabilities["agent.ping"] = new CapabilityOptions { ExampleQuestions = ["Is the laptop on?"] });

        var ping = Assert.Single(provider.Current.Capabilities);
        Assert.Equal("agent", ping.Domain);
        Assert.Equal(AccessLevels.Read, ping.Access);
        Assert.Equal(["Is the laptop on?"], ping.ExampleQuestions);
    }

    [Fact]
    public void Lists_the_domains_and_playbooks_of_the_registry()
    {
        var provider = Build(o =>
        {
            o.Domains["code"] = new DomainOptions { Description = "Source code.", UseWhen = "Questions about implementation.", ExampleQuestions = ["Where is X done?"] };
            o.Playbooks["impact"] = Playbook("test.fake", "code.search");
        }, capabilities: [Fake(), new FakeCapability("code.search"), new FakeCapability("code.map")]);

        var catalog = provider.Current;
        Assert.Equal(["code", "test"], catalog.Domains.Select(d => d.Name));
        var code = catalog.Domains[0];
        Assert.Equal("Source code.", code.Description);
        Assert.Equal("Questions about implementation.", code.UseWhen);
        Assert.Equal(["Where is X done?"], code.ExampleQuestions);
        Assert.Equal(["code.map", "code.search"], code.Capabilities);
        var playbook = Assert.Single(catalog.Playbooks);
        Assert.Equal(["test.fake", "code.search"], playbook.Steps.Select(s => s.Capability));
    }

    [Theory]
    [MemberData(nameof(ChangesTheCloudCanSee))]
    public void Version_changes_when_anything_the_cloud_may_ask_for_changes(string change, Action<AgentBridgeOptions> configure)
    {
        var baseline = Build(AddDemoRepo, capabilities: [Fake()]).Current.CatalogVersion;

        var changed = Build(o => { AddDemoRepo(o); configure(o); }, capabilities: [Fake()]).Current.CatalogVersion;

        Assert.True(baseline != changed, $"The catalog version should change when: {change}");
    }

    [Fact]
    public void Version_changes_when_an_arg_changes()
    {
        static FakeCapability WithArg(ParameterDescriptor parameter) => Fake(d => d with
        {
            Parameters = new Dictionary<string, ParameterDescriptor> { ["count"] = parameter },
        });

        var baseline = Build(capabilities: [WithArg(new ParameterDescriptor { Type = ParameterTypes.Integer })]).Current.CatalogVersion;

        Assert.NotEqual(baseline, Build(capabilities: [WithArg(new ParameterDescriptor { Type = ParameterTypes.Number })]).Current.CatalogVersion);
        Assert.NotEqual(baseline, Build(capabilities: [WithArg(new ParameterDescriptor { Type = ParameterTypes.Integer, Required = true })]).Current.CatalogVersion);
        Assert.NotEqual(baseline, Build(capabilities: [WithArg(new ParameterDescriptor { Type = ParameterTypes.Integer, Description = "How many." })]).Current.CatalogVersion);
        Assert.NotEqual(baseline, Build(capabilities: [WithArg(new ParameterDescriptor { Type = ParameterTypes.Integer, Pattern = "^[0-9]+$" })]).Current.CatalogVersion);
    }

    [Fact]
    public void Version_changes_when_a_capability_is_added_or_removed()
    {
        var one = Build(capabilities: [Fake()]).Current.CatalogVersion;
        var two = Build(capabilities: [Fake(), new FakeCapability("test.other")]).Current.CatalogVersion;
        var none = Build(o => o.Capabilities["test.fake"] = new CapabilityOptions { Enabled = false }, capabilities: [Fake()]).Current.CatalogVersion;

        Assert.Equal(3, new[] { one, two, none }.Distinct().Count());
    }

    [Fact]
    public void Version_changes_when_the_example_changes()
    {
        var first = Build(capabilities: [Fake(d => d with { ExampleArgs = new JsonObject { ["a"] = 1 } })]).Current.CatalogVersion;
        var second = Build(capabilities: [Fake(d => d with { ExampleArgs = new JsonObject { ["a"] = 2 } })]).Current.CatalogVersion;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Example_is_a_complete_request_for_a_capability_without_target()
    {
        var example = Assert.Single(Build().Current.Capabilities).Example!;

        Assert.Equal(1, example["schemaVersion"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(example["requestId"]!.GetValue<string>()));
        Assert.Equal("agent.ping", example["capability"]!.GetValue<string>());
        Assert.False(example.ContainsKey("target"));
        Assert.Equal("hello", example["args"]!["echo"]!.GetValue<string>());
    }

    [Fact]
    public void Example_requests_deserialize_as_agent_requests()
    {
        var example = Assert.Single(Build().Current.Capabilities).Example!;

        var request = example.Deserialize<AgentRequest>(JsonDefaults.Options)!;

        Assert.Equal(AgentRequest.CurrentSchemaVersion, request.SchemaVersion);
        Assert.Equal("agent.ping", request.Capability);
        Assert.Equal(JsonValueKind.Object, request.Args!.Value.ValueKind);
    }

    [Fact]
    public void Example_names_the_first_target_when_the_capability_needs_one()
    {
        var fake = Fake(d => d with { RequiresTarget = true });
        var provider = Build(o =>
        {
            o.Targets["Zeta"] = new TargetOptions { Path = SecretPath, Description = "z" };
            AddDemoRepo(o);
        }, capabilities: [fake]);

        var example = Assert.Single(provider.Current.Capabilities).Example!;

        Assert.Equal("DemoRepo", example["target"]!.GetValue<string>());
    }

    [Fact]
    public void Example_uses_a_placeholder_target_when_none_is_configured()
    {
        var example = Assert.Single(Build(capabilities: [Fake(d => d with { RequiresTarget = true })]).Current.Capabilities).Example!;

        Assert.Equal("<target name>", example["target"]!.GetValue<string>());
    }

    [Fact]
    public void Example_uses_empty_args_when_the_capability_has_no_example_args()
    {
        var example = Assert.Single(Build(capabilities: [Fake()]).Current.Capabilities).Example!;

        var args = Assert.IsType<JsonObject>(example["args"]);
        Assert.Empty(args);
    }

    [Fact]
    public void Example_is_deterministic()
    {
        var first = Assert.Single(Build().Current.Capabilities).Example!.ToJsonString();
        var second = Assert.Single(Build().Current.Capabilities).Example!.ToJsonString();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Several_providers_can_share_one_registry()
    {
        var options = CreateOptions();
        var registry = CreateRegistry(options, new PingCapability(options, new FakeTimeProvider(Now)));

        var first = new CatalogProvider(registry, options, new FakeTimeProvider(Now));
        var second = new CatalogProvider(registry, options, new FakeTimeProvider(Now));

        Assert.Equal(first.Current.CatalogVersion, second.Current.CatalogVersion);
        Assert.Equal("hello", registry.All[0].Descriptor.ExampleArgs!["echo"]!.GetValue<string>());
    }

    [Fact]
    public void Current_is_built_once()
    {
        var provider = Build();

        Assert.Same(provider.Current, provider.Current);
    }

    [Fact]
    public void Catalog_serializes_to_the_documented_shape()
    {
        var json = JsonSerializer.Serialize(Build(AddDemoRepo).Current, JsonDefaults.Options);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(12, root.GetProperty("catalogVersion").GetString()!.Length);
        Assert.Equal("test-agent", root.GetProperty("agent").GetProperty("id").GetString());
        Assert.Equal("DemoRepo", root.GetProperty("targets")[0].GetProperty("name").GetString());
        var ping = root.GetProperty("capabilities")[0];
        Assert.Equal("agent.ping", ping.GetProperty("name").GetString());
        Assert.Equal("agent", ping.GetProperty("domain").GetString());
        Assert.Equal("read", ping.GetProperty("access").GetString());
        Assert.Equal(JsonValueKind.Array, ping.GetProperty("exampleQuestions").ValueKind);
        Assert.Equal("string", ping.GetProperty("args").GetProperty("echo").GetProperty("type").GetString());
        Assert.Equal("agent.ping", ping.GetProperty("example").GetProperty("capability").GetString());
        var agent = root.GetProperty("domains")[0];
        Assert.Equal("agent", agent.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(agent.GetProperty("description").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(agent.GetProperty("useWhen").GetString()));
        Assert.Equal(JsonValueKind.Array, agent.GetProperty("exampleQuestions").ValueKind);
        Assert.Equal("agent.ping", agent.GetProperty("capabilities")[0].GetString());
        Assert.Equal(0, root.GetProperty("playbooks").GetArrayLength());
    }

    [Fact]
    public void Playbooks_serialize_to_the_documented_shape()
    {
        var json = JsonSerializer.Serialize(Build(o => o.Playbooks["check"] = Playbook("agent.ping")).Current, JsonDefaults.Options);

        using var document = JsonDocument.Parse(json);
        var playbook = document.RootElement.GetProperty("playbooks")[0];
        Assert.Equal("check", playbook.GetProperty("name").GetString());
        Assert.Equal("Check the test things.", playbook.GetProperty("goal").GetString());
        Assert.Equal(JsonValueKind.Array, playbook.GetProperty("exampleQuestions").ValueKind);
        var step = playbook.GetProperty("steps")[0];
        Assert.Equal("agent.ping", step.GetProperty("capability").GetString());
        Assert.Equal("Run agent.ping.", step.GetProperty("purpose").GetString());
    }

    public sealed class Readme
    {
        private static readonly CatalogProvider Provider = Build(
            o =>
            {
                AddDemoRepo(o);
                o.Playbooks["backlog-check"] = new PlaybookOptions
                {
                    Goal = "Check that the backlog\nis reachable.",
                    Steps = [new() { Capability = "agent.ping", Purpose = "Reach the agent." }, new() { Capability = "test.fake", Purpose = "Export." }],
                };
            },
            capabilities: [Fake(d => d with { RequiresTarget = true, Description = "Exports the backlog.\nAs Markdown." }), new FakeCapability("agent.ping")]);

        private readonly string _readme = Provider.RenderReadme();

        [Fact]
        public void Names_the_agent_and_the_current_catalog_version()
        {
            Assert.Contains("`test-agent`", _readme, StringComparison.Ordinal);
            Assert.Contains($"`{Provider.Current.CatalogVersion}`", _readme, StringComparison.Ordinal);
        }

        [Fact]
        public void Explains_where_requests_and_responses_go()
        {
            Assert.Contains("inbox/{requestId}.json", _readme, StringComparison.Ordinal);
            Assert.Contains("responses/{requestId}.json", _readme, StringComparison.Ordinal);
            Assert.Contains("ONE new file", _readme, StringComparison.Ordinal);
            Assert.Contains("never reuse", _readme, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("1 to 128 characters", _readme, StringComparison.Ordinal);
            Assert.Contains("catalog.json", _readme, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("schemaVersion")]
        [InlineData("requestId")]
        [InlineData("capability")]
        [InlineData("target")]
        [InlineData("args")]
        [InlineData("expiresAtUtc")]
        [InlineData("purpose")]
        [InlineData("correlationId")]
        public void Documents_every_request_field(string field)
        {
            Assert.Contains($"| `{field}` |", _readme, StringComparison.Ordinal);
        }

        [Fact]
        public void Contains_a_complete_ping_example_that_is_a_valid_request()
        {
            var start = _readme.IndexOf("```json", StringComparison.Ordinal) + "```json".Length;
            var end = _readme.IndexOf("```", start, StringComparison.Ordinal);

            var request = JsonSerializer.Deserialize<AgentRequest>(_readme[start..end], JsonDefaults.Options)!;

            Assert.Equal(AgentRequest.CurrentSchemaVersion, request.SchemaVersion);
            Assert.True(Naming.IsValidRequestId(request.RequestId));
            Assert.Equal("agent.ping", request.Capability);
        }

        [Theory]
        [InlineData("succeeded")]
        [InlineData("failed")]
        [InlineData("rejected")]
        [InlineData("timedOut")]
        [InlineData("interrupted")]
        public void Lists_every_final_status(string status)
        {
            Assert.Contains($"| `{status}` |", _readme, StringComparison.Ordinal);
        }

        [Fact]
        public void Final_statuses_in_the_readme_match_the_job_status_enum()
        {
            var statuses = Enum.GetNames<JobStatus>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]);

            foreach (var status in statuses)
            {
                Assert.Contains($"| `{status}` |", _readme, StringComparison.Ordinal);
            }
            Assert.Contains("not finished yet", _readme, StringComparison.Ordinal);
        }

        [Fact]
        public void Explains_every_error_code()
        {
            var codes = typeof(ErrorCodes)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => (string)f.GetRawConstantValue()!);

            foreach (var code in codes)
            {
                Assert.Contains($"| `{code}` |", _readme, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Explains_the_catalog_version_rule_and_artifacts()
        {
            Assert.Contains("catalogVersion", _readme, StringComparison.Ordinal);
            Assert.Contains("Re-read `catalog.json`", _readme, StringComparison.Ordinal);
            Assert.Contains("artifacts/", _readme, StringComparison.Ordinal);
            Assert.Contains("relative to the root", _readme, StringComparison.Ordinal);
        }

        [Fact]
        public void Warns_against_commands_paths_and_credentials()
        {
            Assert.Contains("Never put commands, file paths or credentials in a request", _readme, StringComparison.Ordinal);
        }

        [Fact]
        public void Lists_the_current_capabilities_on_single_lines()
        {
            Assert.Contains("- `agent.ping` — Description of agent.ping.", _readme, StringComparison.Ordinal);
            Assert.Contains("- `test.fake` — Exports the backlog. As Markdown. (needs a `target`)", _readme, StringComparison.Ordinal);
        }

        [Fact]
        public void Explains_how_to_choose_what_to_call()
        {
            var start = _readme.IndexOf("## Choosing what to call", StringComparison.Ordinal);
            var end = _readme.IndexOf("## Sending a request", StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, "The choosing section comes before the protocol.");
            var section = _readme[start..end];

            Assert.Contains("1. If the user's goal matches a playbook (its `goal`, `useWhen` or `exampleQuestions`), follow its `steps` in order, one request per step.", section, StringComparison.Ordinal);
            Assert.Contains("Use the results of earlier steps to fill the arguments of later ones. Skip a step only when it is clearly not needed.", section, StringComparison.Ordinal);
            Assert.Contains("2. Otherwise pick the domain whose `description`, `useWhen` or `exampleQuestions` match the question, then the capability inside it", section, StringComparison.Ordinal);
            Assert.Contains("3. If nothing matches, tell the user", section, StringComparison.Ordinal);
            Assert.Contains("`access`", section, StringComparison.Ordinal);
        }

        [Fact]
        public void Lists_the_playbooks_with_their_goals_and_steps()
        {
            Assert.Contains("## Playbooks right now", _readme, StringComparison.Ordinal);
            Assert.Contains("- `backlog-check` — Check that the backlog is reachable. Steps: `agent.ping` → `test.fake`.", _readme, StringComparison.Ordinal);
        }

        [Fact]
        public void Lists_the_domains_with_their_descriptions_and_capabilities()
        {
            var agent = Provider.Current.Domains.Single(d => d.Name == "agent");

            Assert.Contains("## Domains right now", _readme, StringComparison.Ordinal);
            Assert.Contains($"- `agent` — {agent.Description} Capabilities: `agent.ping`.", _readme, StringComparison.Ordinal);
            Assert.Contains("- `test` — Capabilities: `test.fake`.", _readme, StringComparison.Ordinal);
        }

        [Fact]
        public void Says_so_when_no_playbook_is_configured()
        {
            Assert.Contains("No playbooks are configured", Build().RenderReadme(), StringComparison.Ordinal);
        }

        [Fact]
        public void Says_so_when_no_capability_is_enabled()
        {
            var provider = Build(o => o.Capabilities["test.fake"] = new CapabilityOptions { Enabled = false }, capabilities: [Fake()]);

            Assert.Contains("No capabilities are enabled", provider.RenderReadme(), StringComparison.Ordinal);
        }

        [Fact]
        public void Uses_unix_line_endings_and_is_stable()
        {
            Assert.DoesNotContain('\r', _readme);
            Assert.Equal(_readme, Provider.RenderReadme());
        }
    }
}
