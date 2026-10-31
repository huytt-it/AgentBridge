using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentBridge.Contracts;

namespace AgentBridge.Processing;

/// <summary>Checks request args against a capability's declared parameters. Messages are written for an LLM.</summary>
internal static class ArgsValidator
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    /// <summary>
    /// On success <paramref name="validated"/> is the args object (an empty object when absent or null),
    /// with integral numbers such as 5.0 rewritten as 5 so capabilities can read them with GetInt64.
    /// </summary>
    public static bool TryValidate(
        JsonElement? args,
        IReadOnlyDictionary<string, ParameterDescriptor> parameters,
        out JsonElement validated,
        out string error)
    {
        validated = EmptyObject;
        error = "";

        if (args is null || args.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            args = EmptyObject;
        }
        var value = args.Value;
        if (value.ValueKind != JsonValueKind.Object)
        {
            error = $"args must be a JSON object, not {Describe(value.ValueKind)}. {DescribeAccepted(parameters)}";
            return false;
        }

        // Exact (case-sensitive) names, because capabilities read args with JsonElement.TryGetProperty.
        var known = parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        Dictionary<string, long>? integralRewrites = null;

        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                problems.Add($"args.{property.Name} appears more than once");
                continue;
            }
            if (!known.TryGetValue(property.Name, out var parameter))
            {
                problems.Add($"args.{property.Name} is not an accepted arg");
                continue;
            }

            var element = property.Value;
            if (!HasType(element, parameter.Type, out var integral))
            {
                problems.Add(element.ValueKind switch
                {
                    JsonValueKind.Null => $"args.{property.Name} is null; omit optional args instead of sending null",
                    JsonValueKind.Number when parameter.Type == ParameterTypes.Integer =>
                        $"args.{property.Name} must be a whole number between {long.MinValue} and {long.MaxValue}",
                    _ => $"args.{property.Name} must be {Article(parameter.Type)} {parameter.Type}, not {Describe(element.ValueKind)}",
                });
                continue;
            }
            if (integral is { } rewritten && !element.TryGetInt64(out _))
            {
                (integralRewrites ??= new Dictionary<string, long>(StringComparer.Ordinal))[property.Name] = rewritten;
            }
            if (parameter.Type == ParameterTypes.String && !string.IsNullOrEmpty(parameter.Pattern))
            {
                var problem = CheckPattern(property.Name, element.GetString()!, parameter.Pattern);
                if (problem is not null)
                {
                    problems.Add(problem);
                }
            }
        }

        foreach (var (name, parameter) in known)
        {
            if (parameter.Required && !seen.Contains(name))
            {
                problems.Add($"args.{name} is required");
            }
        }

        if (problems.Count > 0)
        {
            error = $"Invalid args: {string.Join("; ", problems)}. {DescribeAccepted(parameters)}";
            return false;
        }

        validated = integralRewrites is null ? value : Rewrite(value, integralRewrites);
        return true;
    }

    /// <summary>"Accepted args: name (string, required), ..." or a note that the capability takes none.</summary>
    public static string DescribeAccepted(IReadOnlyDictionary<string, ParameterDescriptor> parameters)
    {
        if (parameters.Count == 0)
        {
            return "This capability takes no args; send an empty object or omit args.";
        }
        var parts = parameters
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key} ({p.Value.Type}, {(p.Value.Required ? "required" : "optional")}"
                + (string.IsNullOrEmpty(p.Value.Pattern) ? ")" : $", pattern {p.Value.Pattern})"));
        return $"Accepted args: {string.Join(", ", parts)}.";
    }

    private static bool HasType(JsonElement element, string type, out long? integral)
    {
        integral = null;
        switch (type)
        {
            case ParameterTypes.String:
                return element.ValueKind == JsonValueKind.String;
            case ParameterTypes.Number:
                return element.ValueKind == JsonValueKind.Number;
            case ParameterTypes.Boolean:
                return element.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case ParameterTypes.Object:
                return element.ValueKind == JsonValueKind.Object;
            case ParameterTypes.Array:
                return element.ValueKind == JsonValueKind.Array;
            case ParameterTypes.Integer:
                if (element.ValueKind != JsonValueKind.Number)
                {
                    return false;
                }
                if (element.TryGetInt64(out var whole))
                {
                    integral = whole;
                    return true;
                }
                // 5.0 and 5e2 are integers too, as long as they fit in a long.
                if (element.TryGetDecimal(out var number) && number == decimal.Truncate(number)
                    && number >= long.MinValue && number <= long.MaxValue)
                {
                    integral = (long)number;
                    return true;
                }
                return false;
            default:
                throw new InvalidOperationException($"Parameter type '{type}' is not supported.");
        }
    }

    private static string? CheckPattern(string name, string value, string pattern)
    {
        try
        {
            return Regex.IsMatch(value, $"^(?:{pattern})\\z", RegexOptions.CultureInvariant, PatternTimeout)
                ? null
                : $"args.{name} must match the pattern {pattern}";
        }
        catch (RegexMatchTimeoutException)
        {
            return $"args.{name} could not be checked against the pattern {pattern}; send a shorter, simpler value";
        }
    }

    private static JsonElement Rewrite(JsonElement value, Dictionary<string, long> integers)
    {
        var node = JsonNode.Parse(value.GetRawText())!.AsObject();
        foreach (var (name, number) in integers)
        {
            node[name] = number;
        }
        return JsonSerializer.SerializeToElement(node, JsonDefaults.Options);
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "an unsupported value",
    };

    private static string Article(string type) => type is ParameterTypes.Integer or ParameterTypes.Object or ParameterTypes.Array ? "an" : "a";
}
