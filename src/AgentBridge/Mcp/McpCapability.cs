using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Capabilities;
using AgentBridge.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AgentBridge.Mcp;

/// <summary>Calls one approved tool of a configured MCP server. Built from configuration by <see cref="McpCapabilityFactory"/>.</summary>
internal sealed class McpCapability : ICapability
{
    public const string ResultFileName = "result.md";
    public const int MaxInlineTextBytes = 64 * 1024;
    public const int MaxErrorLength = 2000;

    private readonly McpServerManager _servers;
    private readonly string _server;
    private readonly string _tool;
    private readonly IReadOnlyDictionary<string, McpFixedArg> _fixedArgs;
    private readonly ILogger _logger;

    public McpCapability(
        CapabilityDescriptor descriptor,
        McpServerManager servers,
        string server,
        string tool,
        IReadOnlyDictionary<string, McpFixedArg> fixedArgs,
        ILogger logger)
    {
        Descriptor = descriptor;
        _servers = servers;
        _server = server;
        _tool = tool;
        _fixedArgs = fixedArgs;
        _logger = logger;
    }

    public CapabilityDescriptor Descriptor { get; }

    public async Task<CapabilityResult> ExecuteAsync(CapabilityContext context, CancellationToken cancellationToken)
    {
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (context.Args.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in context.Args.EnumerateObject())
            {
                arguments[property.Name] = property.Value;
            }
        }
        // Applied last, so the cloud can never override them.
        foreach (var (name, value) in _fixedArgs)
        {
            arguments[name] = value.Resolve(context.TargetPath);
        }

        CallToolResult result;
        try
        {
            result = await _servers.CallToolAsync(_server, _tool, arguments, cancellationToken);
        }
        catch (McpServerException ex)
        {
            // The message names local commands and may quote server stderr: it stays in the local log.
            _logger.LogError(ex, "{Capability} failed because its MCP server could not be used: {Reason}", Descriptor.Name, ex.Message);
            return CapabilityResult.Failure(ErrorCodes.ExecutionFailed,
                $"The local tool server behind '{Descriptor.Name}' stopped or could not be started, so the request did not complete. "
                + "It is started again on the next request. Try again with a new requestId; if it keeps failing, ask the user to check the agent.");
        }
        catch (McpProtocolException ex)
        {
            _logger.LogWarning("{Capability}: MCP server '{Server}' rejected the call to '{Tool}': {Reason}", Descriptor.Name, _server, _tool, ex.Message);
            return CapabilityResult.Failure(ErrorCodes.ExecutionFailed, $"The tool rejected the request: {Shorten(ex.Message)}");
        }

        return await MapResultAsync(result, context, cancellationToken);
    }

    private async Task<CapabilityResult> MapResultAsync(CallToolResult result, CapabilityContext context, CancellationToken cancellationToken)
    {
        var content = result.Content ?? [];
        var text = string.Join("\n", content.OfType<TextContentBlock>().Select(t => t.Text));

        if (result.IsError == true)
        {
            return CapabilityResult.Failure(ErrorCodes.ExecutionFailed,
                string.IsNullOrWhiteSpace(text) ? $"'{Descriptor.Name}' reported an error without details." : Shorten(text));
        }

        // Servers repeat structured content as text for older clients, so the text is not needed then.
        if (result.StructuredContent is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } structured)
        {
            return CapabilityResult.Success(JsonSerializer.SerializeToNode(structured, JsonDefaults.Options));
        }

        var data = new JsonObject();
        string? summary = null;
        var textBytes = Encoding.UTF8.GetByteCount(text);
        if (textBytes <= MaxInlineTextBytes)
        {
            data["text"] = text;
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(context.ArtifactDirectory, ResultFileName), text, new UTF8Encoding(false), cancellationToken);
            data["artifact"] = ResultFileName;
            data["textBytes"] = textBytes;
            summary = $"The result is {(textBytes / 1024.0).ToString("0", CultureInfo.InvariantCulture)} KB of text, "
                + $"too large to return inline; it is published as the artifact {ResultFileName}.";
        }

        var skipped = content.Where(c => c is not TextContentBlock).GroupBy(c => c.Type).ToList();
        if (skipped.Count > 0)
        {
            var counts = new JsonObject();
            foreach (var group in skipped.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                counts[group.Key] = group.Count();
            }
            data["skippedContent"] = counts;
            summary ??= $"The tool also returned {skipped.Sum(g => g.Count())} non-text content item(s), which this agent does not forward.";
        }

        return CapabilityResult.Success(data, summary);
    }

    private static string Shorten(string value) =>
        value.Length <= MaxErrorLength ? value : value[..(MaxErrorLength - 3)] + "...";
}

/// <summary>A FixedArgs value: a JSON value checked against the tool's schema, or a string containing {targetPath}.</summary>
internal sealed class McpFixedArg
{
    public const string TargetPathPlaceholder = "{targetPath}";

    private readonly JsonElement _value;
    private readonly string? _template;

    private McpFixedArg(JsonElement value, string? template)
    {
        _value = value;
        _template = template;
    }

    public static McpFixedArg Value(JsonNode? value) => new(JsonSerializer.SerializeToElement(value, JsonDefaults.Options), null);

    public static McpFixedArg Template(string template) => new(default, template);

    public JsonElement Resolve(string? targetPath)
    {
        if (_template is null)
        {
            return _value;
        }
        if (targetPath is null)
        {
            throw new InvalidOperationException($"A FixedArgs value uses {TargetPathPlaceholder} but the request has no target.");
        }
        return JsonSerializer.SerializeToElement(_template.Replace(TargetPathPlaceholder, targetPath, StringComparison.Ordinal), JsonDefaults.Options);
    }
}
