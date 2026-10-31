using System.Text.Json.Nodes;

namespace AgentBridge.Contracts;

/// <summary>One capability argument, as declared by the capability and published in catalog.json.</summary>
public sealed class ParameterDescriptor
{
    /// <summary>One of <see cref="ParameterTypes"/>.</summary>
    public string Type { get; init; } = ParameterTypes.String;

    public bool Required { get; init; }

    public string Description { get; init; } = "";

    /// <summary>Regex a string value must match in full. Published so the cloud can produce valid values.</summary>
    public string? Pattern { get; init; }

    public JsonNode? Example { get; init; }
}

public static class ParameterTypes
{
    public const string String = "string";
    public const string Integer = "integer";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Object = "object";
    public const string Array = "array";
}

/// <summary>
/// Values of the "output" field: the format of a capability's main result. json and text results are inline in
/// the response; markdown and csv results are published as artifacts.
/// </summary>
public static class OutputFormats
{
    public const string Json = "json";
    public const string Text = "text";
    public const string Markdown = "markdown";
    public const string Csv = "csv";
}
