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
  logs/                    agentbridge-yyyyMMdd.log, the worker's log (see "Logging")
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
- `catalog.json` describes capabilities in business terms (`description`, `useWhen`, `exampleQuestions`, args,
  example), grouped into domains, plus playbooks (see "Domains, playbooks and access").

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
| Business description of a group of capabilities | An entry under `AgentBridge:Domains` |
| Multi-step business goal | An entry under `AgentBridge:Playbooks` |

## Domains, playbooks and access

The cloud picks what to call only from `catalog.json`, so the catalog groups capabilities the way a business user
thinks. All of it is built by `CapabilityRegistry` at startup and published by `CatalogProvider`.

- **Domain**: the first segment of a capability name (`backlog.export` is in `backlog`). Optional configuration under
  `AgentBridge:Domains:{name}` adds `Description`, `UseWhen` and `ExampleQuestions`; without it the domain is still
  published by name (the built-in `agent` domain has its own texts). `Enabled: false` disables every capability of the
  domain before any factory runs, so no tool or MCP server starts for them. Domains without enabled capabilities are
  left out.
- **Playbook**: `AgentBridge:Playbooks:{name}` describes a business goal as ordered steps (`Capability`, `Purpose`).
  It is guidance only: there is no workflow engine; the cloud sends one request per step and every capability stays
  callable on its own. A step naming no capability is a startup error; a step whose capability is disabled leaves the
  playbook out of the catalog, and `validate` says so.
- **Access**: every capability has an access level, its own, else its domain's, else `read`. Only `read` can be
  enabled; anything else is a startup error until a local approval step exists (rule 6 below). It is the place where
  permissions can be added later.

Catalog additions: `domains`, `playbooks`, and `domain`, `access`, `exampleQuestions` on every capability. All are
sorted deterministically and part of `catalogVersion`.

## Logging

Everything is logged through `ILogger`. The worker (the default command) logs to the console and also to
`{LocalDataRoot}/logs/agentbridge-yyyyMMdd.log`, one file per local calendar day, so a worker without a screen leaves
something to read. `validate`, `run` and `mcp-tools` log to stderr only.

- `FileLoggerProvider` (`src/AgentBridge/Logging/`, provider alias `File`) writes one line per entry,
  `2026-11-02T10:15:30.123+07:00 INFO  AgentBridge.Processing.RequestProcessor: <message>`, with an exception on the
  following lines. Levels follow `Logging:LogLevel`, and `Logging:File:LogLevel` for this provider alone.
- The file stays open and is flushed per entry, so it can be read while the worker runs (`Get-Content`).
- `AgentBridge:LogRetentionDays` (default 14, 1–3650): when a day's file is first opened, files named
  `agentbridge-yyyyMMdd.log` and dated more than that many days back are deleted. Nothing else in the folder is touched.
- Logging never breaks the worker: an IO error is swallowed after one line on stderr, and the next entry tries again.
- The folder is resolved on the first entry from the validated options, so an invalid configuration (including a
  `LocalDataRoot` inside `BoxRoot`) writes no log and ends in `Program.cs`'s `Configuration is invalid:` message.
  Logs never go into the Box folder.

How to read and manage the log: [operations.md](operations.md).

## Artifacts

A capability writes large results (Markdown, CSV...) into `CapabilityContext.ArtifactDirectory`.
When the job succeeds, the pipeline copies them to `artifacts/{requestId}/` in Box **before** the response,
and the response lists them (`artifacts[].path`, relative to the Box root). Over `MaxArtifactMegabytes`
the job fails with `artifact_too_large` and nothing is published. `json`/`text` results stay inline in `result`.

## Configuration files

`appsettings.json` (shared, in git) → `appsettings.{Environment}.json` → `appsettings.Local.json`
(machine-specific: BoxRoot, targets, tools, secrets; git ignores it) → environment variables → command line.
`BoxRoot` is optional: when it is empty the bridge uses `AgentBridge` inside Box Drive (`%USERPROFILE%\Box`), the
folder name the cloud's standing instruction expects, so a new machine needs no configuration to connect.

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

From the repository root. The Development config keeps job state in `src/AgentBridge/.local/data` (`dotnet run`
starts in the project folder); the Box folder is `BoxRoot`, or
Box Drive's `%USERPROFILE%\Box\AgentBridge` when it is not set (startup fails if neither exists).
`scripts/selftest.ps1` uses a simulated Box folder under `.local/selftest`.

```bash
dotnet run --project src/AgentBridge
dotnet run --project src/AgentBridge -- validate
dotnet run --project src/AgentBridge -- run agent.ping
dotnet test
```

Relative `BoxRoot` / `LocalDataRoot` resolve against the current directory; use absolute paths elsewhere.
