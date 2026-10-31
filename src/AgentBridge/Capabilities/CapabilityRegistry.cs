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

    private readonly IReadOnlyList<ICapability> _all;
    private readonly Dictionary<string, ICapability> _byName;

    /// <exception cref="OptionsValidationException">Lists every configuration problem found, not just the first.</exception>
    public CapabilityRegistry(
        IEnumerable<ICapability> nativeCapabilities,
        IEnumerable<ICapabilityFactory> factories,
        IOptions<AgentBridgeOptions> options)
    {
        var configured = options.Value.Capabilities;
        var errors = new List<string>();

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
            if (!entry.Enabled)
            {
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
            Validate(created.Descriptor, errors);
            enabled.Add(created);
        }

        foreach (var capability in native.Values)
        {
            configured.TryGetValue(capability.Descriptor.Name, out var entry);
            var effective = Apply(capability, entry);
            Validate(effective.Descriptor, errors);
            if (entry is not { Enabled: false })
            {
                enabled.Add(effective);
            }
        }

        if (errors.Count > 0)
        {
            throw new OptionsValidationException(AgentBridgeOptions.SectionName, typeof(AgentBridgeOptions), errors);
        }

        _all = enabled.OrderBy(c => c.Descriptor.Name, StringComparer.Ordinal).ToList();
        _byName = _all.ToDictionary(c => c.Descriptor.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ICapability> All => _all;

    public bool TryGet(string name, [NotNullWhen(true)] out ICapability? capability)
    {
        capability = null;
        return name is not null && _byName.TryGetValue(name, out capability);
    }

    private static string ConfigKey(string name) => $"{AgentBridgeOptions.SectionName}:Capabilities:{name}";

    private static ICapability Apply(ICapability capability, CapabilityOptions? entry)
    {
        if (entry is null || (entry.Description is null && entry.UseWhen is null && entry.TimeoutSeconds is null))
        {
            return capability;
        }

        var descriptor = capability.Descriptor;
        if (entry.Description is not null)
        {
            descriptor = descriptor with { Description = entry.Description };
        }
        if (entry.UseWhen is not null)
        {
            descriptor = descriptor with { UseWhen = string.IsNullOrWhiteSpace(entry.UseWhen) ? null : entry.UseWhen };
        }
        if (entry.TimeoutSeconds is { } seconds)
        {
            descriptor = descriptor with { Timeout = TimeSpan.FromSeconds(seconds) };
        }
        return new OverriddenCapability(capability, descriptor);
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

    // camelCase or snake_case: MCP tools often use names like file_path.
    [GeneratedRegex(@"^[a-z][A-Za-z0-9_]*\z")]
    private static partial Regex ParameterNamePattern();
}
