# Adding a capability

How to give the cloud agent (Microsoft 365 Copilot) a new thing to ask this machine for. Written for a human or an AI
assistant doing the work. Read [architecture.md](architecture.md) first; this page is the procedure.

A capability is one named, read-only operation. The cloud sees only its name, description and arguments in
`catalog.json`; the pipeline looks it up by name and runs it.

## Before you write code

1. **Ask the user which tools to expose.** Do not decide this yourself, and do not add a capability just because the
   tool exists on the machine.
2. **Check the rules** (see [Rules](#rules)). If the tool writes, deletes, pushes, deploys or changes a database, stop:
   it stays disabled until a local approval step exists.
3. **Choose the name.** Lowercase dotted segments, business-oriented: `backlog.export`, `code.business-rules`, not
   `run-tool` or `mcp.call`. The pattern is `^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)+$` (`Naming.IsValidCapabilityName`).
   A name is a contract with the cloud: **never rename a capability, add a new one instead.**
4. **Write the description and `useWhen` for a model that has never seen this system.**
   - `Description`: what the capability returns, in business terms. One or two sentences.
     Good: "Exports the open backlog items of a project as Markdown, grouped by epic, with status and priority."
     Bad: "Runs backlog-cli.exe export with --format md."
   - `UseWhen`: the situation in which the cloud should pick it, or when it should *not*.
     Good: "When you need the current scope of a project before analysing requirements."
   - No local paths, executable names, command lines, credentials or machine names in either text.
     The cloud decides which capability to call from these two sentences alone.
5. **Decide the inputs.** Each argument gets a camelCase name, a type (`string`, `integer`, `number`, `boolean`,
   `object`, `array`), a one-line `Description`, `Required`, and for strings a `Pattern` when the accepted values are
   narrow (the pattern is published so the cloud can produce valid values, and enforced locally).
   If the capability works on a local project or folder, set `RequiresTarget = true` and read the folder from
   `context.TargetPath`; never take a path from `Args`.

## Steps

### 1. Implement `ICapability`

Create one file in `src/AgentBridge/Capabilities/Native/`. Constructor parameters may only be services already
registered in `ServiceCollectionExtensions` (`IOptions<AgentBridgeOptions>`, `TimeProvider`, `ILogger<T>`,
`AgentBridgePaths`, ...). Look at `PingCapability` for a complete small example.

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Contracts;

namespace AgentBridge.Capabilities.Native;

// Illustration only: not part of the code base. CountItemsAsync stands in for the real work.
internal sealed class BacklogSummaryCapability : ICapability
{
    public CapabilityDescriptor Descriptor { get; } = new()
    {
        Name = "backlog.summary",
        Description = "Counts the open backlog items of a project by status and priority.",
        UseWhen = "When you need a quick view of the size and shape of a project's backlog before asking for details.",
        RequiresTarget = true,
        Parameters = new Dictionary<string, ParameterDescriptor>
        {
            ["includeClosed"] = new()
            {
                Type = ParameterTypes.Boolean,
                Description = "Also count closed items. Default false.",
            },
        },
        Output = OutputFormats.Json,
        Timeout = TimeSpan.FromSeconds(30),
        ExampleArgs = new JsonObject { ["includeClosed"] = false },
    };

    public async Task<CapabilityResult> ExecuteAsync(CapabilityContext context, CancellationToken cancellationToken)
    {
        // context.TargetPath is the absolute folder behind the requested target; context.Args is already validated.
        var counts = await CountItemsAsync(context.TargetPath!, cancellationToken);
        if (counts is null)
        {
            return CapabilityResult.Failure(
                ErrorCodes.ExecutionFailed,
                "The target has no backlog folder. Ask the user to check that the project is set up for backlog export.");
        }

        return CapabilityResult.Success(JsonSerializer.SerializeToNode(counts, JsonDefaults.Options), "Backlog summary");
    }
}
```

What to know when implementing `ExecuteAsync`:

- Return `CapabilityResult.Success(data, summary)` or `CapabilityResult.Failure(code, message)` for expected problems.
  Throw only for bugs.
- Error messages are read by an LLM that must fix its next request: say what went wrong and what would work.
- Honour `cancellationToken`; it fires on timeout and on shutdown.
- Keep `Data` small enough to read in one response file. Scratch files and logs go under `context.JobDirectory`
  (local, never synced). If a capability needs to hand over a file (`CapabilityResult.Artifacts`), look at how the
  pipeline publishes artifacts in `src/AgentBridge/Processing` and ask the user before relying on it.
- Never return local paths, credentials or machine details in `Data` or in messages.
- `Descriptor` is read once at startup; keep it constant.

### 2. Register it

One line in `src/AgentBridge/ServiceCollectionExtensions.cs`, next to the others:

```csharp
services.AddSingleton<ICapability, BacklogSummaryCapability>();
```

That is the only wiring. The registry finds it, validates the descriptor and the catalog lists it.

### 3. Configure it (optional)

Native capabilities are enabled by default and need no configuration. An entry in `appsettings.json` (or the
environment-specific file) under `AgentBridge:Capabilities` can switch one off or reword it without a rebuild:

```json
"Capabilities": {
  "agent.ping": { "Enabled": false },
  "backlog.summary": {
    "Description": "Counts the open backlog items of a project by status and priority.",
    "UseWhen": "When you need a quick view of the size of a project's backlog.",
    "TimeoutSeconds": 60
  }
}
```

| Key | Effect |
|---|---|
| `Type` | `native` (default). `cli` and `mcp` entries define a whole capability; see [onboarding-cli-tool.md](onboarding-cli-tool.md) and [onboarding-mcp-server.md](onboarding-mcp-server.md). |
| `Enabled` | `false` removes the capability from the registry and the catalog. |
| `Description`, `UseWhen` | Replace the built-in text in `catalog.json`. A blank `UseWhen` clears it. |
| `TimeoutSeconds` | Replaces the built-in timeout. Must be greater than 0. |

If the capability needs a target, add it under `AgentBridge:Targets`. The `Path` stays on this machine; only the
name and `Description` reach the cloud, so never put a path in the description.

### 4. Check it locally

From the repository root:

```bash
dotnet run --project src/AgentBridge -- validate
dotnet run --project src/AgentBridge -- run backlog.summary --target MyProject --args '{"includeClosed":false}'
```

In Windows PowerShell 5.1 escape the quotes inside the JSON (`'{\"includeClosed\":false}'`).

`validate` loads the configuration, builds the registry and prints the catalog. It reports every configuration problem
at once (invalid names, duplicate names, bad argument names or patterns, unknown config entries). `run` executes the
capability without Box and prints the response the cloud would receive. Read both outputs as the cloud would:
is it clear from `description` and `useWhen` alone when to call this, and what comes back?

### 5. Write tests

In `tests/AgentBridge.Tests/Capabilities/`, namespace `AgentBridge.Tests.Capabilities`, xUnit. Use `FakeTimeProvider`
for time and `TempDirectory` for any files. Cover at least:

- the descriptor (name, no local paths in texts, parameter types and patterns, timeout);
- the happy path result (`Data` fields, `Summary`);
- each expected failure and the error code and message it returns;
- argument edge cases that the descriptor's `Pattern` is meant to allow or reject.

`PingCapabilityTests` is the template. Then run:

```bash
dotnet build AgentBridge.sln
dotnet test AgentBridge.sln
```

Both must pass with 0 warnings on .NET 8 and .NET 10. Use C# 12 and no .NET 9+ APIs (see "Compatibility" in
[architecture.md](architecture.md)).

### 6. Restart the worker

`catalog.json` and `README.md` are written once at startup. Restart the worker so the cloud sees the new capability.
The catalog version changes whenever a description, argument, timeout, target or capability changes, and every response
carries it, so a cloud agent holding an old catalog is told to re-read it.

## CLI tools and MCP tools need no code

Most tools do not need a native capability at all:

- A command-line tool is one `Type: "cli"` entry in `appsettings.Local.json`. See [onboarding-cli-tool.md](onboarding-cli-tool.md).
- A tool of a local MCP server is one `Type: "mcp"` entry, plus the server under `McpServers`.
  See [onboarding-mcp-server.md](onboarding-mcp-server.md).

Write a native capability only when a tool needs logic that configuration cannot express, such as several steps
or parsing its output into structured data.

## Rules

1. **No generic capabilities.** Never expose "run a shell command", "run any script", "read any file", "call any
   MCP tool" or anything where the cloud supplies a command, an executable or a path. Every capability does one named
   thing with validated arguments.
2. **No local details in anything the cloud sees.** Descriptions, `UseWhen`, argument descriptions, examples, error
   messages and results contain no paths, user names, machine names, credentials or tokens. Targets are names.
3. **Side effects stay disabled.** A capability that writes, deletes, pushes, deploys or changes a database must not be
   enabled until a local approval step exists (rule 6 in [architecture.md](architecture.md)).
4. **Ask the user first.** The user decides which tools the cloud may reach. Propose; do not add silently.
5. **Never rename.** Names are a contract with the cloud. To change what a capability does in an incompatible way, add
   a new capability and retire the old one.
6. **Validation is local.** `catalog.json` is discovery, not authorization; the pipeline validates every request against
   the descriptor. Write `Pattern`s and parameter types so that invalid input is rejected before `ExecuteAsync` runs.
7. **Business language.** Describe results in the terms of the analyst who reads them, not in terms of the tool or the
   code behind it.

## Checklist

- [ ] The user approved exposing this tool.
- [ ] The capability is read-only (or stays disabled).
- [ ] The name is final and business-oriented; `Description` and `UseWhen` are clear without any other context.
- [ ] No local details in any text the cloud sees.
- [ ] One class in `Capabilities/Native/` and one `AddSingleton<ICapability, ...>` line.
- [ ] `validate` is clean and `run <name>` returns what you expect.
- [ ] Tests added; `dotnet build` shows 0 warnings and `dotnet test` passes.
- [ ] The worker was restarted so `catalog.json` is current.
