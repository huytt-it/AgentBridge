using AgentBridge.Contracts;

namespace AgentBridge.Tests.Foundation;

public class NamingTests
{
    [Theory]
    [InlineData("7f3c2a9e-1b4d-4c8a-9e2f-0a1b2c3d4e5f")]
    [InlineData("req-001")]
    [InlineData("a")]
    [InlineData("job_2026.10.31")]
    public void Accepts_safe_request_ids(string id) => Assert.True(Naming.IsValidRequestId(id));

    [Theory]
    [InlineData("")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("trailing.")]
    [InlineData("-leading")]
    [InlineData("has space")]
    [InlineData("line\n")]
    [InlineData("CON")]
    [InlineData("nul.json")]
    [InlineData("COM1")]
    public void Rejects_unsafe_request_ids(string id) => Assert.False(Naming.IsValidRequestId(id));

    [Fact]
    public void Limits_request_ids_to_128_characters()
    {
        Assert.True(Naming.IsValidRequestId(new string('a', 128)));
        Assert.False(Naming.IsValidRequestId(new string('a', 129)));
    }

    [Theory]
    [InlineData("agent.ping")]
    [InlineData("backlog.export")]
    [InlineData("code.business-rules")]
    [InlineData("dotnet.test.v2")]
    public void Accepts_dotted_capability_names(string name) => Assert.True(Naming.IsValidCapabilityName(name));

    [Theory]
    [InlineData("ping")]
    [InlineData("Agent.Ping")]
    [InlineData("agent..ping")]
    [InlineData("agent.ping.")]
    [InlineData(".agent")]
    [InlineData("agent.1ping")]
    public void Rejects_other_capability_names(string name) => Assert.False(Naming.IsValidCapabilityName(name));

    [Theory]
    [InlineData("DemoRepo", true)]
    [InlineData("backlog-main", true)]
    [InlineData("1st", false)]
    [InlineData("a/b", false)]
    [InlineData("AUX", false)]
    public void Validates_target_names(string name, bool expected) => Assert.Equal(expected, Naming.IsValidTargetName(name));
}
