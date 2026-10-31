# AgentBridge architecture

AgentBridge lets a cloud agent (Microsoft 365 Copilot) ask this machine for data that only exists locally
(local tools, local MCP servers, Copilot CLI) through a Box-synced folder. The cloud never connects to the
machine: the worker polls the folder, runs named capabilities and writes the results back.

Scope today: a simple single-user MVP. One flow — read a request, run a capability, write a response.

## Flow

```text
inbox/*.json → parse → dedup → claim (local job folder) → validate → execute → save response locally
             → publish responses/{requestId}.json → move the request to archive/
```

- Parse fails and the file is younger than `IncompleteFileGraceSeconds`: skip it, it may still be syncing.
  Older: move it to `dead-letter/` with the reason.
- Same `requestId` again with identical bytes: duplicate. Archive it, make sure the response is published, never run again.
- Same `requestId` with different bytes: dead-letter (`duplicate_conflict`); the original job is untouched.
- Startup recovery: claimed but no response → publish `interrupted` (never re-run, the run may have had side effects);
  response saved but not published → publish it.
- Jobs run one at a time.

## Folders

```text
<BoxRoot>/                 synced with the cloud
  inbox/  responses/  artifacts/  archive/  dead-letter/
  catalog.json  README.md  written at startup

<LocalDataRoot>/           never synced (default %LOCALAPPDATA%/AgentBridge)
  jobs/{requestId}/        request.json, response.json, published.marker, logs
  staging/                 files are written here, then moved into Box
  agentbridge.lock         one worker per data root
```

## Contracts (`src/AgentBridge/Contracts`)

- Request: `schemaVersion` (1), `requestId`, `capability`; optional `target`, `args` (object), `expiresAtUtc`,
  `purpose`, `correlationId`. Unknown fields are ignored, so new fields never break older agents.
- Response statuses `succeeded | failed | rejected | timedOut | interrupted` are all final. A later version may add
  non-final statuses; readers treat unknown statuses as "not finished yet".
- Every response carries `catalogVersion`. Error messages are written for an LLM to fix its request:
  say what was wrong and what is accepted.
- `catalog.json` describes capabilities in business terms (`description`, `useWhen`, args, example).

## Extension point

`ICapability` (`src/AgentBridge/Capabilities/ICapability.cs`). The pipeline only does a registry lookup.
Config entries with `Type` "cli" or "mcp" are turned into capabilities by an `ICapabilityFactory`
(`src/AgentBridge/Cli/`, `src/AgentBridge/Mcp/`) while the registry is built at startup.

| Add | How |
|---|---|
| Native .NET tool | One class implementing `ICapability` + one line in `ServiceCollectionExtensions` |
| CLI tool | A `Type: "cli"` entry under `AgentBridge:Capabilities` |
| MCP server (STDIO) | An entry under `AgentBridge:McpServers` |
| Tool of a configured MCP server | A `Type: "mcp"` entry under `AgentBridge:Capabilities` (this is the approval) |
| Tool with special needs | One class implementing `ICapability` |

## Artifacts

A capability writes large results (Markdown, CSV...) into `CapabilityContext.ArtifactDirectory`.
When the job succeeds, the pipeline copies them to `artifacts/{requestId}/` in Box **before** the response,
and the response lists them (`artifacts[].path`, relative to the Box root). Over `MaxArtifactMegabytes`
the job fails with `artifact_too_large` and nothing is published. `json`/`text` results stay inline in `result`.

## Configuration files

`appsettings.json` (shared, in git) → `appsettings.{Environment}.json` → `appsettings.Local.json`
(machine-specific: BoxRoot, targets, tools, secrets; git ignores it) → environment variables → command line.

## Rules that are not negotiable

1. A request names a capability. It never carries a command line, an executable or a path.
2. `target` is a name resolved through `AgentBridge:Targets`; local paths are never sent or published.
3. Request ids become file names: check `Naming.IsValidRequestId` before using one in a path.
4. Job state, logs and full outputs stay under the local data root. Box gets responses and approved artifacts only.
5. Every request is validated locally. `catalog.json` is discovery, not authorization.
6. Capabilities with side effects (writes, deletes, push, deploy, DB changes) stay disabled until a local approval step exists.

## Compatibility: .NET 8 and .NET 10

- The app targets `net8.0` with `RollForward=Major`: builds with SDK 8 or 10, runs on runtime 8 or 10.
- Tests target `net8.0`, plus `net10.0` when built with SDK 10.
- C# 12 only (pinned in `Directory.Build.props`). No .NET 9+ APIs, e.g. `Convert.ToHexStringLower`,
  `System.Threading.Lock`, `Task.WhenEach`.
- `Microsoft.Extensions.*` are on 10.0.x (required by the MCP SDK `ModelContextProtocol.Core` 2.3.0). They still
  target `net8.0`, and every analyzer in the package graph needs Roslyn 4.8 at most, which all .NET 8 SDKs ship.
  Check this again before adding or upgrading a package.
- Classic `.sln` solution file (SDK 8 cannot read `.slnx`).

## Running

From the repository root (Development config keeps everything under `.local/`):

```bash
dotnet run --project src/AgentBridge
dotnet run --project src/AgentBridge -- validate
dotnet run --project src/AgentBridge -- run agent.ping
dotnet test
```

Relative `BoxRoot` / `LocalDataRoot` resolve against the current directory; use absolute paths elsewhere.
