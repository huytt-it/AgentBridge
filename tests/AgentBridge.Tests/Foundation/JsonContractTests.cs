using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Contracts;

namespace AgentBridge.Tests.Foundation;

public class JsonContractTests
{
    [Fact]
    public void Request_reads_camel_case_and_ignores_unknown_fields()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "requestId": "r1",
              "capability": "agent.ping",
              "target": "DemoRepo",
              "args": { "echo": "hi" },
              "expiresAtUtc": "2026-10-31T12:00:00Z",
              "conversationId": "added-by-a-later-version"
            }
            """;

        var request = JsonSerializer.Deserialize<AgentRequest>(json, JsonDefaults.Options)!;

        Assert.Equal("r1", request.RequestId);
        Assert.Equal("DemoRepo", request.Target);
        Assert.Equal(JsonValueKind.Object, request.Args!.Value.ValueKind);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), request.ExpiresAtUtc);
    }

    [Fact]
    public void Request_without_optional_fields_has_nulls()
    {
        var request = JsonSerializer.Deserialize<AgentRequest>("""{"requestId":"r1","capability":"agent.ping"}""", JsonDefaults.Options)!;

        Assert.Equal(0, request.SchemaVersion);
        Assert.Null(request.Args);
        Assert.Null(request.Target);
    }

    [Fact]
    public void Response_writes_camel_case_status_and_omits_nulls()
    {
        var response = new AgentResponse
        {
            RequestId = "r1",
            Status = JobStatus.TimedOut,
            CompletedAtUtc = DateTimeOffset.UnixEpoch,
            Agent = new AgentInfo("a", "1.0"),
            CatalogVersion = "abc",
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, JsonDefaults.Options));

        Assert.Equal("timedOut", document.RootElement.GetProperty("status").GetString());
        Assert.False(document.RootElement.TryGetProperty("error", out _));
        Assert.Equal("a", document.RootElement.GetProperty("agent").GetProperty("id").GetString());
    }

    [Fact]
    public void Response_round_trips()
    {
        var response = new AgentResponse
        {
            RequestId = "r1",
            Capability = "agent.ping",
            Status = JobStatus.Rejected,
            CompletedAtUtc = DateTimeOffset.UnixEpoch,
            Agent = new AgentInfo("a", "1.0"),
            CatalogVersion = "abc",
            Result = new JsonObject { ["n"] = 1 },
            Artifacts = [new ArtifactRef("artifacts/r1/result.md", "text/markdown", 10)],
            Error = new ErrorInfo(ErrorCodes.UnknownCapability, "nope"),
        };

        var copy = JsonSerializer.Deserialize<AgentResponse>(JsonSerializer.Serialize(response, JsonDefaults.Options), JsonDefaults.Options)!;

        Assert.Equal(JobStatus.Rejected, copy.Status);
        Assert.Equal(response.Agent, copy.Agent);
        Assert.Equal(response.Error, copy.Error);
        Assert.Equal(response.Artifacts, copy.Artifacts);
        Assert.Equal(1, copy.Result!["n"]!.GetValue<int>());
    }
}
