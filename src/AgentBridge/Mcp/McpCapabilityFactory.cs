using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace AgentBridge.Mcp;

/// <summary>
/// Turns AgentBridge:Capabilities entries with Type "mcp" into capabilities that call one approved MCP tool.
/// Starts each referenced server and reads its tool list, so it blocks for up to twice the server's StartupTimeoutSeconds.
/// </summary>
internal sealed class McpCapabilityFactory : ICapabilityFactory
{
    public const int DefaultTimeoutSeconds = 60;

    private readonly McpServerManager _servers;
    private readonly AgentBridgeOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpCapabilityFactory> _logger;
    private readonly Dictionary<string, (IReadOnlyList<Tool>? Tools, string? Error)> _toolLists = new(StringComparer.OrdinalIgnoreCase);

    public McpCapabilityFactory(McpServerManager servers, IOptions<AgentBridgeOptions> options, ILoggerFactory loggerFactory)
    {
        _servers = servers;
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<McpCapabilityFactory>();
    }

    public string Type => "mcp";

    public ICapability? Create(string name, CapabilityOptions options, IList<string> errors)
    {
        var key = ConfigKey(name);
        if (string.IsNullOrWhiteSpace(options.Server))
        {
            errors.Add($"{key}:Server is required: the name of a server under AgentBridge:McpServers. {ServerList()}");
            return null;
        }
        var server = _options.McpServers.Keys.FirstOrDefault(s => string.Equals(s, options.Server, StringComparison.OrdinalIgnoreCase));
        if (server is null)
        {
            errors.Add($"{key}:Server '{options.Server}' is not configured under AgentBridge:McpServers. {ServerList()}");
            return null;
        }
        if (!_options.McpServers[server].Enabled)
        {
            errors.Add($"{key}:Server '{server}' is disabled (AgentBridge:McpServers:{server}:Enabled is false); enable the server or disable this capability.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(options.Tool))
        {
            errors.Add($"{key}:Tool is required: a tool name exactly as 'AgentBridge mcp-tools {server}' lists it.");
            return null;
        }

        var (tools, error) = GetTools(server);
        if (tools is null)
        {
            errors.Add($"{key}:Server '{server}' could not be used: {error}");
            return null;
        }
        var tool = tools.FirstOrDefault(t => t.Name == options.Tool);
        if (tool is null)
        {
            var available = tools.Count == 0 ? "it lists no tools" : $"its tools are {string.Join(", ", tools.Select(t => t.Name))}";
            errors.Add($"{key}:Tool '{options.Tool}' is not a tool of MCP server '{server}'; {available}. "
                + $"Run 'AgentBridge mcp-tools {server}' to see each tool's description and input schema.");
            return null;
        }
        return Create(name, options, server, tool, errors);
    }

    /// <summary>Builds the capability for a tool already listed by its server. Problems go to <paramref name="errors"/>.</summary>
    internal ICapability? Create(string name, CapabilityOptions options, string server, Tool tool, IList<string> errors)
    {
        var key = ConfigKey(name);
        var errorCount = errors.Count;
        var properties = McpToolSchema.Properties(tool.InputSchema).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var required = McpToolSchema.Required(tool.InputSchema);
        var propertyList = properties.Count == 0 ? "it has no properties" : $"its properties are {string.Join(", ", properties.Keys)}";

        var fixedArgs = new Dictionary<string, McpFixedArg>(StringComparer.Ordinal);
        foreach (var (argName, value) in options.FixedArgs)
        {
            if (!properties.TryGetValue(argName, out var propertySchema))
            {
                errors.Add($"{key}:FixedArgs:{argName} is not a property of tool '{tool.Name}'; {propertyList}.");
                continue;
            }
            if (options.Params.ContainsKey(argName))
            {
                errors.Add($"{key}:FixedArgs:{argName} is also in Params; a fixed arg is never sent by the cloud, so remove it from Params.");
                continue;
            }
            var type = McpToolSchema.MapType(propertySchema);
            if (value.Contains(McpFixedArg.TargetPathPlaceholder, StringComparison.Ordinal))
            {
                if (!options.RequiresTarget)
                {
                    errors.Add($"{key}:FixedArgs:{argName} uses {McpFixedArg.TargetPathPlaceholder}, which needs a target: "
                        + "set RequiresTarget to true and configure the folders under AgentBridge:Targets.");
                }
                else if (type is not (null or ParameterTypes.String))
                {
                    errors.Add($"{key}:FixedArgs:{argName} uses {McpFixedArg.TargetPathPlaceholder}, but the tool expects {type}, not a path string.");
                }
                fixedArgs[argName] = McpFixedArg.Template(value);
                continue;
            }
            if (!McpToolSchema.TryConvert(value, type, out var node))
            {
                errors.Add($"{key}:FixedArgs:{argName} is '{value}', which is not a valid {type} as the tool's input schema requires.");
                continue;
            }
            fixedArgs[argName] = McpFixedArg.Value(node);
        }

        // Params, when present, is the allowlist; otherwise every property that is not fixed is exposed.
        var allowlisted = options.Params.Count > 0;
        if (allowlisted)
        {
            foreach (var paramName in options.Params.Keys.Where(p => !properties.ContainsKey(p)))
            {
                errors.Add($"{key}:Params:{paramName} is not a property of tool '{tool.Name}'; {propertyList}.");
            }
        }
        var exposedNames = allowlisted
            ? options.Params.Keys.Where(p => properties.ContainsKey(p) && !fixedArgs.ContainsKey(p))
            : properties.Keys.Where(p => !fixedArgs.ContainsKey(p));

        var parameters = new Dictionary<string, ParameterDescriptor>(StringComparer.Ordinal);
        foreach (var paramName in exposedNames)
        {
            var propertySchema = properties[paramName];
            options.Params.TryGetValue(paramName, out var configured);
            var type = McpToolSchema.MapType(propertySchema);
            if (type is null)
            {
                if (allowlisted || required.Contains(paramName))
                {
                    errors.Add($"{key}: property '{paramName}' of tool '{tool.Name}' has no single simple type in its input schema "
                        + $"({Shorten(propertySchema.GetRawText())}); set it in FixedArgs instead"
                        + (allowlisted ? " and remove it from Params." : "."));
                }
                else
                {
                    _logger.LogWarning("Capability '{Capability}': optional property '{Property}' of MCP tool '{Tool}' has no single simple type and is not offered to the cloud.",
                        name, paramName, tool.Name);
                }
                continue;
            }

            JsonNode? example = null;
            if (configured?.Example is { } exampleText && !McpToolSchema.TryConvert(exampleText, type, out example))
            {
                errors.Add($"{key}:Params:{paramName}:Example '{exampleText}' is not a valid {type}.");
            }
            parameters[paramName] = new ParameterDescriptor
            {
                Type = type,
                Required = required.Contains(paramName) || configured?.Required == true,
                Description = !string.IsNullOrWhiteSpace(configured?.Description)
                    ? configured.Description
                    : McpToolSchema.Description(propertySchema) ?? "",
                Pattern = !string.IsNullOrWhiteSpace(configured?.Pattern) ? configured.Pattern : McpToolSchema.EnumPattern(propertySchema),
                Example = example,
            };
        }

        // Without an allowlist every required property is exposed or fixed, or was reported above.
        if (allowlisted)
        {
            foreach (var requiredName in required.Where(r => properties.ContainsKey(r) && !options.Params.ContainsKey(r) && !options.FixedArgs.ContainsKey(r)))
            {
                errors.Add($"{key}: tool '{tool.Name}' requires '{requiredName}', which is neither in Params nor in FixedArgs; add it to one of them.");
            }
        }

        var exampleArgs = BuildExampleArgs(key, options, parameters, errors);

        var description = options.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            description = tool.Description ?? "";
            if (!string.IsNullOrWhiteSpace(description))
            {
                _logger.LogWarning(
                    "Capability '{Capability}' publishes the description of MCP tool '{Tool}' because {Key}:Description is not set. "
                    + "MCP descriptions are written for coding agents and are untrusted text; write a business description in the configuration.",
                    name, tool.Name, key);
            }
        }

        if (errors.Count > errorCount)
        {
            return null;
        }

        var descriptor = new CapabilityDescriptor
        {
            Name = name,
            Description = description,
            UseWhen = string.IsNullOrWhiteSpace(options.UseWhen) ? null : options.UseWhen,
            RequiresTarget = options.RequiresTarget,
            Parameters = parameters,
            Output = OutputFormats.Json,
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds ?? DefaultTimeoutSeconds),
            ExampleArgs = exampleArgs,
        };
        return new McpCapability(descriptor, _servers, server, tool.Name, fixedArgs, _loggerFactory.CreateLogger<McpCapability>());
    }

    private (IReadOnlyList<Tool>? Tools, string? Error) GetTools(string server)
    {
        if (_toolLists.TryGetValue(server, out var cached))
        {
            return cached;
        }

        var seconds = _options.McpServers[server].StartupTimeoutSeconds;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds * 2));
        (IReadOnlyList<Tool>? Tools, string? Error) result;
        try
        {
            // Runs while the registry is built at startup, outside any synchronization context.
            result = (_servers.ListToolsAsync(server, timeout.Token).GetAwaiter().GetResult(), null);
        }
        catch (McpServerException ex)
        {
            result = (null, ex.Message);
        }
        catch (OperationCanceledException)
        {
            result = (null, $"it did not start and list its tools within {seconds * 2} seconds; "
                + $"raise AgentBridge:McpServers:{server}:StartupTimeoutSeconds if it is slow to start.");
        }
        catch (Exception ex)
        {
            result = (null, $"listing its tools failed: {ex.Message}");
        }
        _toolLists[server] = result;
        return result;
    }

    private static JsonObject? BuildExampleArgs(
        string key, CapabilityOptions options, IReadOnlyDictionary<string, ParameterDescriptor> parameters, IList<string> errors)
    {
        if (options.ExampleArgs.Count == 0)
        {
            var fromParams = parameters.Where(p => p.Value.Example is not null).ToList();
            return fromParams.Count == 0 ? null : new JsonObject(fromParams.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value.Example!.DeepClone())));
        }

        var example = new JsonObject();
        foreach (var (argName, value) in options.ExampleArgs)
        {
            if (!parameters.TryGetValue(argName, out var parameter))
            {
                errors.Add($"{key}:ExampleArgs:{argName} is not an arg the cloud may send; accepted: {string.Join(", ", parameters.Keys)}.");
            }
            else if (!McpToolSchema.TryConvert(value, parameter.Type, out var node))
            {
                errors.Add($"{key}:ExampleArgs:{argName} is '{value}', which is not a valid {parameter.Type}.");
            }
            else
            {
                example[argName] = node;
            }
        }
        return example;
    }

    private string ServerList() => _options.McpServers.Count == 0
        ? "No servers are configured yet."
        : $"Configured servers: {string.Join(", ", _options.McpServers.Keys.Order(StringComparer.OrdinalIgnoreCase))}.";

    private static string ConfigKey(string name) => $"{AgentBridgeOptions.SectionName}:Capabilities:{name}";

    private static string Shorten(string value) => value.Length <= 120 ? value : value[..120] + "...";
}
