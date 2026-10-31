using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentBridge.Contracts;

namespace AgentBridge.Mcp;

/// <summary>Reads the top level of an MCP tool's input schema (a JSON Schema object) in terms of <see cref="ParameterTypes"/>.</summary>
internal static class McpToolSchema
{
    private static readonly string[] SimpleTypes =
    [
        ParameterTypes.String, ParameterTypes.Integer, ParameterTypes.Number,
        ParameterTypes.Boolean, ParameterTypes.Object, ParameterTypes.Array,
    ];

    /// <summary>Top-level properties in schema order, with their own schemas.</summary>
    public static IReadOnlyList<KeyValuePair<string, JsonElement>> Properties(JsonElement inputSchema) =>
        inputSchema.ValueKind == JsonValueKind.Object
        && inputSchema.TryGetProperty("properties", out var properties)
        && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value)).ToList()
            : [];

    public static IReadOnlySet<string> Required(JsonElement inputSchema) =>
        inputSchema.ValueKind == JsonValueKind.Object
        && inputSchema.TryGetProperty("required", out var required)
        && required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The <see cref="ParameterTypes"/> value of a property schema, or null when it has no single simple type.
    /// "null" is ignored in type lists and anyOf/oneOf (nullable values), and a string enum is a string.
    /// </summary>
    public static string? MapType(JsonElement propertySchema)
    {
        if (propertySchema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (propertySchema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String)
            {
                return Simple(type.GetString());
            }
            if (type.ValueKind == JsonValueKind.Array)
            {
                var types = type.EnumerateArray()
                    .Where(t => t.ValueKind == JsonValueKind.String && t.GetString() != "null")
                    .Select(t => t.GetString())
                    .ToList();
                return types.Count == 1 ? Simple(types[0]) : null;
            }
            return null;
        }
        if (StringEnum(propertySchema) is not null)
        {
            return ParameterTypes.String;
        }
        foreach (var keyword in (string[])["anyOf", "oneOf"])
        {
            if (propertySchema.TryGetProperty(keyword, out var alternatives) && alternatives.ValueKind == JsonValueKind.Array)
            {
                var candidates = alternatives.EnumerateArray().Where(a => !IsNullSchema(a)).ToList();
                return candidates.Count == 1 ? MapType(candidates[0]) : null;
            }
        }
        return null;
    }

    public static string? Description(JsonElement propertySchema) =>
        propertySchema.ValueKind == JsonValueKind.Object
        && propertySchema.TryGetProperty("description", out var description)
        && description.ValueKind == JsonValueKind.String
            ? description.GetString()
            : null;

    /// <summary>
    /// A full-match pattern for a string enum, e.g. "asc|desc", so the allowed values reach the cloud and are checked
    /// locally. JSON Schema "pattern" itself is not copied: it is an unanchored ECMAScript regex.
    /// </summary>
    public static string? EnumPattern(JsonElement propertySchema) =>
        StringEnum(propertySchema) is { Count: > 0 } values ? string.Join("|", values.Select(Regex.Escape)) : null;

    /// <summary>Converts a configured string (FixedArgs, examples) to a JSON value of the given type.</summary>
    public static bool TryConvert(string value, string? type, out JsonNode? node)
    {
        node = null;
        switch (type)
        {
            case null:
            case ParameterTypes.String:
                node = JsonValue.Create(value);
                return true;
            case ParameterTypes.Integer:
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    node = JsonValue.Create(integer);
                    return true;
                }
                return false;
            case ParameterTypes.Number:
                if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    node = JsonValue.Create(number);
                    return true;
                }
                return false;
            case ParameterTypes.Boolean:
                if (bool.TryParse(value, out var boolean))
                {
                    node = JsonValue.Create(boolean);
                    return true;
                }
                return false;
            default:
                try
                {
                    node = JsonNode.Parse(value);
                }
                catch (JsonException)
                {
                    return false;
                }
                return (node is JsonObject && type == ParameterTypes.Object) || (node is JsonArray && type == ParameterTypes.Array);
        }
    }

    private static string? Simple(string? type) => SimpleTypes.FirstOrDefault(t => t == type);

    private static List<string>? StringEnum(JsonElement propertySchema) =>
        propertySchema.TryGetProperty("enum", out var values)
        && values.ValueKind == JsonValueKind.Array
        && values.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String)
            ? values.EnumerateArray().Select(v => v.GetString()!).ToList()
            : null;

    private static bool IsNullSchema(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && type.GetString() == "null";
}
