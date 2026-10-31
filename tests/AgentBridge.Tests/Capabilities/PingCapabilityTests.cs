using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Capabilities.Native;
using AgentBridge.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace AgentBridge.Tests.Capabilities;

public sealed class PingCapabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 8, 30, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);

    private PingCapability CreatePing() =>
        new(CapabilityTestSupport.CreateOptions(o => o.AgentId = "laptop-01"), _time);

    private static DateTimeOffset ReadMachineTime(JsonObject data) => data["machineTimeUtc"].Deserialize<DateTimeOffset>(JsonDefaults.Options);

    [Fact]
    public void Descriptor_describes_a_target_free_check_with_an_optional_echo()
    {
        var descriptor = CreatePing().Descriptor;

        Assert.Equal("agent.ping", descriptor.Name);
        Assert.True(Naming.IsValidCapabilityName(descriptor.Name));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Description));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.UseWhen));
        Assert.False(descriptor.RequiresTarget);
        Assert.Equal(OutputFormats.Json, descriptor.Output);
        Assert.Equal(TimeSpan.FromSeconds(10), descriptor.Timeout);

        var echo = Assert.Single(descriptor.Parameters);
        Assert.Equal("echo", echo.Key);
        Assert.Equal(ParameterTypes.String, echo.Value.Type);
        Assert.False(echo.Value.Required);
        Assert.Equal("hello", descriptor.ExampleArgs!["echo"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("line one\nline two")]
    public void Echo_pattern_accepts_ordinary_text(string value)
    {
        Assert.Matches(CreatePing().Descriptor.Parameters["echo"].Pattern!, value);
    }

    [Fact]
    public void Echo_pattern_stops_at_200_characters()
    {
        var pattern = CreatePing().Descriptor.Parameters["echo"].Pattern!;

        Assert.Matches(pattern, new string('x', 200));
        Assert.DoesNotMatch(pattern, new string('x', 201));
        Assert.DoesNotMatch(pattern, new string('x', 200) + "\n");
    }

    [Fact]
    public async Task Returns_agent_id_version_and_machine_time()
    {
        var result = await CreatePing().ExecuteAsync(CapabilityTestSupport.CreateContext(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("pong", result.Summary);
        var data = Assert.IsType<JsonObject>(result.Data);
        Assert.Equal("laptop-01", data["agentId"]!.GetValue<string>());
        Assert.Equal(AgentVersion.Current, data["agentVersion"]!.GetValue<string>());
        Assert.Equal(Now, ReadMachineTime(data));
    }

    [Fact]
    public async Task Machine_time_follows_the_clock()
    {
        var ping = CreatePing();
        _time.Advance(TimeSpan.FromMinutes(5));

        var result = await ping.ExecuteAsync(CapabilityTestSupport.CreateContext(), CancellationToken.None);

        Assert.Equal(Now.AddMinutes(5), ReadMachineTime((JsonObject)result.Data!));
    }

    [Fact]
    public async Task Returns_echo_unchanged_when_given()
    {
        var result = await CreatePing().ExecuteAsync(CapabilityTestSupport.CreateContext("""{ "echo": "hello wörld" }"""), CancellationToken.None);

        Assert.Equal("hello wörld", ((JsonObject)result.Data!)["echo"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "echo": null }""")]
    public async Task Leaves_echo_out_when_not_given(string argsJson)
    {
        var result = await CreatePing().ExecuteAsync(CapabilityTestSupport.CreateContext(argsJson), CancellationToken.None);

        Assert.False(((JsonObject)result.Data!).ContainsKey("echo"));
    }
}
