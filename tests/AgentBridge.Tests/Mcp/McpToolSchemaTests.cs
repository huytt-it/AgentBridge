using System.Text.Json;
using AgentBridge.Contracts;
using AgentBridge.Mcp;

namespace AgentBridge.Tests.Mcp;

public sealed class McpToolSchemaTests
{
    [Theory]
    [InlineData("""{"type":"string"}""", ParameterTypes.String)]
    [InlineData("""{"type":"integer"}""", ParameterTypes.Integer)]
    [InlineData("""{"type":"number"}""", ParameterTypes.Number)]
    [InlineData("""{"type":"boolean"}""", ParameterTypes.Boolean)]
    [InlineData("""{"type":"object","properties":{}}""", ParameterTypes.Object)]
    [InlineData("""{"type":"array","items":{"type":"string"}}""", ParameterTypes.Array)]
    [InlineData("""{"type":["string","null"]}""", ParameterTypes.String)]
    [InlineData("""{"anyOf":[{"type":"integer"},{"type":"null"}]}""", ParameterTypes.Integer)]
    [InlineData("""{"oneOf":[{"type":"null"},{"type":"array"}]}""", ParameterTypes.Array)]
    [InlineData("""{"enum":["asc","desc"]}""", ParameterTypes.String)]
    [InlineData("""{"type":["string","integer"]}""", null)]
    [InlineData("""{"anyOf":[{"type":"string"},{"type":"integer"}]}""", null)]
    [InlineData("""{"type":"null"}""", null)]
    [InlineData("""{"type":"date"}""", null)]
    [InlineData("""{}""", null)]
    [InlineData("""true""", null)]
    public void Maps_a_property_schema_to_a_parameter_type(string schema, string? expected)
    {
        Assert.Equal(expected, McpToolSchema.MapType(Parse(schema)));
    }

    [Fact]
    public void Reads_top_level_properties_in_order_and_the_required_list()
    {
        var schema = Parse("""
            {"type":"object",
             "properties":{"query":{"type":"string","description":"Text."},"max_results":{"type":"integer"}},
             "required":["query",5]}
            """);

        Assert.Equal(["query", "max_results"], McpToolSchema.Properties(schema).Select(p => p.Key));
        Assert.Equal("Text.", McpToolSchema.Description(McpToolSchema.Properties(schema)[0].Value));
        Assert.Null(McpToolSchema.Description(McpToolSchema.Properties(schema)[1].Value));
        Assert.Equal(["query"], McpToolSchema.Required(schema));
    }

    [Fact]
    public void A_schema_without_properties_has_none()
    {
        var schema = Parse("""{"type":"object"}""");

        Assert.Empty(McpToolSchema.Properties(schema));
        Assert.Empty(McpToolSchema.Required(schema));
    }

    [Fact]
    public void A_string_enum_becomes_a_full_match_pattern()
    {
        Assert.Equal(@"asc|desc|a\.b", McpToolSchema.EnumPattern(Parse("""{"type":"string","enum":["asc","desc","a.b"]}""")));
        Assert.Null(McpToolSchema.EnumPattern(Parse("""{"type":"string","pattern":"^a"}""")));
        Assert.Null(McpToolSchema.EnumPattern(Parse("""{"enum":[1,2]}""")));
    }

    [Theory]
    [InlineData("C:/repo", ParameterTypes.String, "\"C:/repo\"")]
    [InlineData("C:/repo", null, "\"C:/repo\"")]
    [InlineData("50", ParameterTypes.Integer, "50")]
    [InlineData("2.5", ParameterTypes.Number, "2.5")]
    [InlineData("true", ParameterTypes.Boolean, "true")]
    [InlineData("""{"a":1}""", ParameterTypes.Object, """{"a":1}""")]
    [InlineData("[1,2]", ParameterTypes.Array, "[1,2]")]
    public void Converts_configured_strings_to_typed_values(string value, string? type, string expectedJson)
    {
        Assert.True(McpToolSchema.TryConvert(value, type, out var node));
        Assert.Equal(expectedJson, node!.ToJsonString());
    }

    [Theory]
    [InlineData("fifty", ParameterTypes.Integer)]
    [InlineData("2.5", ParameterTypes.Integer)]
    [InlineData("yes", ParameterTypes.Boolean)]
    [InlineData("[1]", ParameterTypes.Object)]
    [InlineData("{", ParameterTypes.Array)]
    public void Rejects_configured_strings_of_the_wrong_type(string value, string type)
    {
        Assert.False(McpToolSchema.TryConvert(value, type, out _));
    }

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
