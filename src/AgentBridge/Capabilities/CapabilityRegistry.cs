using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using AgentBridge.Contracts;
using Microsoft.Extensions.Options;

namespace AgentBridge.Capabilities;

internal sealed partial class CapabilityRegistry : ICapabilityRegistry
{
    private static readonly string[] KnownParameterTypes =
    [
        ParameterTypes.String, ParameterTypes.Integer, ParameterTypes.Number,
        ParameterTypes.Boolean, ParameterTypes.Object, ParameterTypes.Array,
    ];

    /// <summary>Texts of the domains of built-in capabilities, used for whatever their configuration leaves out.</summary>
    private static readonly Dictionary<string, DomainOptions> BuiltInDomains = new(StringComparer.Ordinal)
    {
        ["agent"] = new()
        {
            Description = "The local agent itself: whether it is running and reachable through Box.",
            UseWhen = "Before the first request of a session, or when answers seem to stop arriving.",
            ExampleQuestions = ["Is the local machine reachable right now?"],
        },
    };

    private readonly IReadOnlyList<ICapability> _all;
    private readonly Dictionary<string, ICapability> _byName;

    /// <exception cref="OptionsValidationException">Lists every configuration problem found, not just the first.</exception>
    public CapabilityRegistry(
        IEnumerable<ICapability> nativeCapabilities,
        IEnumerable<ICapabilityFactory> factories,
        IOptions<AgentBridgeOptions> options)
    {
        var settings = options.Value;
        var configured = settings.Capabilities;
        var errors = new List<string>();
        var notes = new List<string>();

        var domains = ReadDomains(settings.Domains, errors);

        var native = new Dictionary<string, ICapability>(StringComparer.OrdinalIgnoreCase);
        foreach (var capability in nativeCapabilities)
        {
            if (!native.TryAdd(capability.Descriptor.Name, capability))
            {
                errors.Add($"Capability '{capability.Descriptor.Name}' is registered more than once; names must be unique.");
            }
        }

        var factoryByType = new Dictionary<string, ICapabilityFactory>(StringComparer.OrdinalIgnoreCase);
        foreach (var factory in factories)
        {
            factoryByType.TryAdd(factory.Type, factory);
        }
        var supportedTypes = string.Join(", ", new[] { "native" }.Concat(factoryByType.Keys.Order(StringComparer.Ordinal)));

        var enabled = new List<ICapability>();
        // Capabilities that exist but are switched off, with the reason: a playbook that uses one is left out.
        var disabled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entry) in configured)
        {
            var key = ConfigKey(name);
            if (!Naming.IsValidCapabilityName(name))
            {
                errors.Add($"Capability name '{name}' is invalid: use lowercase dotted names such as 'backlog.export'.");
                continue;
            }

            var type = string.IsNullOrWhiteSpace(entry.Type) ? "native" : entry.Type.Trim().ToLowerInvariant();
            if (type == "native")
            {
                if (!native.ContainsKey(name))
                {
                    errors.Add($"{key}:Type is 'native' but this build has no native capability named '{name}'. "
                        + $"Built in: {string.Join(", ", native.Keys.Order(StringComparer.Ordinal))}.");
                }
                continue;
            }
            if (!factoryByType.TryGetValue(type, out var typeFactory))
            {
                errors.Add($"{key}:Type '{entry.Type}' is unknown; supported types are {supportedTypes}.");
                continue;
            }
            if (native.ContainsKey(name))
            {
                errors.Add($"{key}: '{name}' is the name of a built-in capability; choose another name.");
                continue;
            }

            var domain = domains.GetValueOrDefault(Naming.DomainOf(name));
            var offReason = DisabledReason(name, entry, domain);
            var access = ReadAccess(entry.Access, $"{key}:Access", offReason is null, errors) ?? domain?.Access ?? AccessLevels.Read;
            if (offReason is not null)
            {
                disabled[name] = offReason;
                continue; // never start tools or servers for disabled entries
            }

            var created = typeFactory.Create(name, entry, errors);
            if (created is null)
            {
                continue;
            }
            if (!string.Equals(created.Descriptor.Name, name, StringComparison.Ordinal))
            {
                errors.Add($"{key}: the {type} factory produced a capability named '{created.Descriptor.Name}'.");
                continue;
            }
            var effective = ApplyOverrides(created, entry, isNative: false, access);
            Validate(effective.Descriptor, errors);
            enabled.Add(effective);
        }

        foreach (var capability in native.Values)
        {
            var name = capability.Descriptor.Name;
            configured.TryGetValue(name, out var entry);
            var domain = domains.GetValueOrDefault(Naming.DomainOf(name));
            var offReason = DisabledReason(name, entry, domain);
            var builtInAccess = ReadAccess(capability.Descriptor.Access, $"Capability '{name}': its built-in Access", offReason is null, errors);
            var access = ReadAccess(entry?.Access, $"{ConfigKey(name)}:Access", offReason is null, errors)
                ?? domain?.Access ?? builtInAccess ?? AccessLevels.Read;

            var effective = ApplyOverrides(capability, entry, isNative: true, access);
            Validate(effective.Descriptor, errors);
            if (offReason is null)
            {
                enabled.Add(effective);
            }
            else
            {
                disabled[name] = offReason;
            }
        }

        var all = enabled.OrderBy(c => c.Descriptor.Name, StringComparer.Ordinal).ToList();
        var byName = all.ToDictionary(c => c.Descriptor.Name, StringComparer.OrdinalIgnoreCase);
        var known = native.Keys.Concat(configured.Keys)
            .Where(Naming.IsValidCapabilityName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var publishedDomains = DescribeDomains(all, domains, known, notes);
        var playbooks = BuildPlaybooks(settings.Playbooks, byName, disabled, known, errors, notes);

        if (errors.Count > 0)
        {
            throw new OptionsValidationException(AgentBridgeOptions.SectionName, typeof(AgentBridgeOptions), errors);
        }

        _all = all;
        _byName = byName;
        Domains = publishedDomains;
        Playbooks = playbooks;
        Notes = notes;
    }

    public IReadOnlyList<ICapability> All => _all;

    public IReadOnlyList<CatalogDomain> Domains { get; }

    public IReadOnlyList<CatalogPlaybook> Playbooks { get; }

    public IReadOnlyList<string> Notes { get; }

    public bool TryGet(string name, [NotNullWhen(true)] out ICapability? capability)
    {
        capability = null;
        return name is not null && _byName.TryGetValue(name, out capability);
    }

    private static string ConfigKey(string name) => $"{AgentBridgeOptions.SectionName}:Capabilities:{name}";

    private static string DomainKey(string name) => $"{AgentBridgeOptions.SectionName}:Domains:{name}";

    private static string PlaybookKey(string name) => $"{AgentBridgeOptions.SectionName}:Playbooks:{name}";

    // ---- Domains and access ----

    private sealed record ConfiguredDomain(DomainOptions Options, string? Access);

    private static Dictionary<string, ConfiguredDomain> ReadDomains(Dictionary<string, DomainOptions> configured, List<string> errors)
    {
        var domains = new Dictionary<string, ConfiguredDomain>(StringComparer.Ordinal);
        foreach (var (name, domain) in configured)
        {
            if (!Naming.IsValidDomainName(name))
            {
                errors.Add($"Domain name '{name}' is invalid: use the first segment of its capabilities' names, lowercase letters, "
                    + "digits and '-' starting with a letter, e.g. 'backlog' for 'backlog.export'.");
                continue;
            }
            domains[name] = new ConfiguredDomain(domain, ReadAccess(domain.Access, $"{DomainKey(name)}:Access", domain.Enabled, errors));
        }
        return domains;
    }

    private static string? DisabledReason(string name, CapabilityOptions? entry, ConfiguredDomain? domain)
    {
        if (entry is { Enabled: false })
        {
            return $"is disabled ({ConfigKey(name)}:Enabled is false)";
        }
        if (domain is { Options.Enabled: false })
        {
            var domainName = Naming.DomainOf(name);
            return $"belongs to the disabled domain '{domainName}' ({DomainKey(domainName)}:Enabled is false)";
        }
        return null;
    }

    /// <summary>
    /// The normalized access level, or null when none is set or the value is unknown (reported). Only "read" can be
    /// enabled; "write" is accepted on a disabled entry so that it can be prepared.
    /// </summary>
    private static string? ReadAccess(string? value, string field, bool enabled, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var level = value.Trim().ToLowerInvariant();
        switch (level)
        {
            case AccessLevels.Read:
                return level;
            case AccessLevels.Write:
                if (enabled)
                {
                    errors.Add($"{field} '{value.Trim()}' needs a local approval step, which this version does not have; "
                        + "only 'read' capabilities can be enabled.");
                }
                return level;
            default:
                errors.Add($"{field} '{value.Trim()}' is unknown; the access levels are 'read' and 'write', and only 'read' can be enabled.");
                return null;
        }
    }

    private static List<CatalogDomain> DescribeDomains(
        IReadOnlyList<ICapability> enabled, Dictionary<string, ConfiguredDomain> configured, HashSet<string> known, List<string> notes)
    {
        foreach (var name in configured.Keys.Order(StringComparer.Ordinal))
        {
            if (!known.Any(capability => Naming.DomainOf(capability).Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                notes.Add($"Domain '{name}' is configured, but no capability name starts with '{name}.'; it is left out of the catalog.");
            }
        }

        return enabled
            .GroupBy(c => Naming.DomainOf(c.Descriptor.Name), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var options = configured.GetValueOrDefault(group.Key)?.Options;
                var builtIn = BuiltInDomains.GetValueOrDefault(group.Key);
                var questions = Clean(options?.ExampleQuestions);
                return new CatalogDomain
                {
                    Name = group.Key,
                    Description = Text(options?.Description) ?? Text(builtIn?.Description),
                    UseWhen = Text(options?.UseWhen) ?? Text(builtIn?.UseWhen),
                    ExampleQuestions = questions.Count > 0 ? questions : Clean(builtIn?.ExampleQuestions),
                    Capabilities = group.Select(c => c.Descriptor.Name).Order(StringComparer.Ordinal).ToList(),
                };
            })
            .ToList();
    }

    // ---- Playbooks ----

    private static List<CatalogPlaybook> BuildPlaybooks(
        Dictionary<string, PlaybookOptions> configured,
        Dictionary<string, ICapability> enabled,
        Dictionary<string, string> disabled,
        HashSet<string> known,
        List<string> errors,
        List<string> notes)
    {
        var playbooks = new List<CatalogPlaybook>();
        foreach (var (name, playbook) in configured.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var key = PlaybookKey(name);
            if (!Naming.IsValidPlaybookName(name))
            {
                errors.Add($"Playbook name '{name}' is invalid: use lowercase letters, digits and '-' starting with a letter, e.g. 'backlog-review'.");
                continue;
            }
            if (!playbook.Enabled)
            {
                continue;
            }

            var errorCount = errors.Count;
            if (string.IsNullOrWhiteSpace(playbook.Goal))
            {
                errors.Add($"{key}:Goal is required: the business goal the playbook reaches, in the words of a business user.");
            }
            if (playbook.Steps.Count == 0)
            {
                errors.Add($"{key}:Steps needs at least one step, each with a Capability and a Purpose.");
            }

            var steps = new List<CatalogPlaybookStep>();
            string? leftOutBecause = null;
            for (var i = 0; i < playbook.Steps.Count; i++)
            {
                var step = playbook.Steps[i];
                var stepKey = $"{key}:Steps:{i}";
                var number = i + 1;
                if (string.IsNullOrWhiteSpace(step.Purpose))
                {
                    errors.Add($"{stepKey}:Purpose is required: say what step {number} of playbook '{name}' contributes to the goal.");
                }
                if (string.IsNullOrWhiteSpace(step.Capability))
                {
                    errors.Add($"{stepKey}:Capability is required: the capability that step {number} of playbook '{name}' calls.");
                    continue;
                }

                var capabilityName = step.Capability.Trim();
                if (enabled.TryGetValue(capabilityName, out var capability))
                {
                    steps.Add(new CatalogPlaybookStep(capability.Descriptor.Name, step.Purpose ?? ""));
                }
                else if (disabled.TryGetValue(capabilityName, out var reason))
                {
                    leftOutBecause ??= $"step {number} uses '{capabilityName}', which {reason}";
                }
                else if (!known.Contains(capabilityName))
                {
                    errors.Add($"{stepKey}:Capability '{capabilityName}' (step {number} of playbook '{name}') is not a capability of this agent. "
                        + $"Capabilities: {string.Join(", ", known.Order(StringComparer.Ordinal))}.");
                }
                // Otherwise the capability is configured but broken, and its own error is already reported.
            }

            if (errors.Count > errorCount)
            {
                continue;
            }
            if (leftOutBecause is not null)
            {
                notes.Add($"Playbook '{name}' is left out of the catalog: {leftOutBecause}.");
                continue;
            }
            playbooks.Add(new CatalogPlaybook
            {
                Name = name,
                Goal = playbook.Goal!,
                UseWhen = Text(playbook.UseWhen),
                ExampleQuestions = Clean(playbook.ExampleQuestions),
                Steps = steps,
            });
        }
        return playbooks;
    }

    // ---- Capabilities ----

    /// <summary>
    /// Applies configured texts and the effective access level. Returns the capability itself when nothing changes.
    /// A cli or mcp factory has already used Description, UseWhen and TimeoutSeconds; for a native capability they
    /// replace the built-in values here.
    /// </summary>
    private static ICapability ApplyOverrides(ICapability capability, CapabilityOptions? entry, bool isNative, string access)
    {
        var original = capability.Descriptor;
        var descriptor = original;
        if (entry is not null)
        {
            if (isNative && entry.Description is not null)
            {
                descriptor = descriptor with { Description = entry.Description };
            }
            if (isNative && entry.UseWhen is not null)
            {
                descriptor = descriptor with { UseWhen = Text(entry.UseWhen) };
            }
            if (isNative && entry.TimeoutSeconds is { } seconds)
            {
                descriptor = descriptor with { Timeout = TimeSpan.FromSeconds(seconds) };
            }
            var questions = Clean(entry.ExampleQuestions);
            if (questions.Count > 0)
            {
                descriptor = descriptor with { ExampleQuestions = questions };
            }
        }
        if (!string.Equals(descriptor.Access, access, StringComparison.Ordinal))
        {
            descriptor = descriptor with { Access = access };
        }
        return ReferenceEquals(descriptor, original) ? capability : new OverriddenCapability(capability, descriptor);
    }

    private static void Validate(CapabilityDescriptor descriptor, List<string> errors)
    {
        var name = descriptor.Name;
        var key = ConfigKey(name);

        if (!Naming.IsValidCapabilityName(name))
        {
            errors.Add($"Capability name '{name}' is invalid: use lowercase dotted names such as 'backlog.export'.");
        }
        if (string.IsNullOrWhiteSpace(descriptor.Description))
        {
            errors.Add($"{key}:Description is empty. Every capability needs a description the cloud can understand.");
        }
        if (descriptor.Timeout <= TimeSpan.Zero)
        {
            errors.Add($"{key}:TimeoutSeconds must be greater than 0.");
        }

        foreach (var (parameterName, parameter) in descriptor.Parameters)
        {
            if (!ParameterNamePattern().IsMatch(parameterName))
            {
                errors.Add($"Capability '{name}': parameter '{parameterName}' must be camelCase or snake_case: letters, digits and '_', starting with a lowercase letter.");
            }
            if (!KnownParameterTypes.Contains(parameter.Type))
            {
                errors.Add($"Capability '{name}': parameter '{parameterName}' has unknown type '{parameter.Type}'; use one of {string.Join(", ", KnownParameterTypes)}.");
            }
            if (parameter.Pattern is not null)
            {
                try
                {
                    _ = new Regex(parameter.Pattern);
                }
                catch (ArgumentException ex)
                {
                    errors.Add($"Capability '{name}': parameter '{parameterName}' has a Pattern that is not a valid regular expression: {ex.Message}");
                }
            }
        }
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static List<string> Clean(IEnumerable<string?>? values) =>
        values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList() ?? [];

    // camelCase or snake_case: MCP tools often use names like file_path.
    [GeneratedRegex(@"^[a-z][A-Za-z0-9_]*\z")]
    private static partial Regex ParameterNamePattern();
}
