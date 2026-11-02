# AgentBridge

Local capability runtime for Microsoft 365 Copilot. The cloud drops JSON requests into a Box-synced folder;
this worker runs named capabilities on the machine and writes JSON responses back. The cloud never connects
to the machine.

- AI agents start here: [AGENTS.md](AGENTS.md)
- Set up a machine: [docs/setup.md](docs/setup.md)
- Connect a CLI tool: [docs/onboarding-cli-tool.md](docs/onboarding-cli-tool.md)
- Connect an MCP server: [docs/onboarding-mcp-server.md](docs/onboarding-mcp-server.md)
- Connect Microsoft 365 Copilot: [docs/cloud-agent.md](docs/cloud-agent.md)
- Design and rules: [docs/architecture.md](docs/architecture.md)
- Write a native capability: [docs/adding-capabilities.md](docs/adding-capabilities.md)

## Requirements

.NET 8 or .NET 10 (SDK to build, runtime to run). The app targets `net8.0` and rolls forward to .NET 10
when .NET 8 is not installed.

## Quick start

1. Install Box Drive and sign in.
2. `dotnet run --project src/AgentBridge` from the repository root. It uses `%USERPROFILE%\Box\AgentBridge` as the
   folder shared with the cloud and creates it.
3. Paste the standing instruction from [docs/cloud-agent.md](docs/cloud-agent.md) into the cloud agent. It already uses
   the folder name `AgentBridge`.

No configuration is needed until you connect tools. If your organisation does not allow folders at the root of Box,
set `BoxRoot` to a folder it allows and change the folder name in the cloud instruction.

## Configure

Put this machine's settings in `src/AgentBridge/appsettings.Local.json` (copy `appsettings.Local.example.json`;
git ignores the copy). Environment variables such as `AgentBridge__BoxRoot` and command-line arguments override it.
Full walkthrough: [docs/setup.md](docs/setup.md).

| Setting | Meaning |
|---|---|
| `BoxRoot` | The Box Drive folder shared with the cloud. Default `%USERPROFILE%\Box\AgentBridge`; startup fails when it is not set and Box Drive is not installed. |
| `LocalDataRoot` | Job state and logs. Default `%LOCALAPPDATA%\AgentBridge`. Must be outside `BoxRoot`. |
| `AgentId` | Name of this machine in responses. Default: the machine name. |
| `PollSeconds` | How often the inbox is checked. Default 5. |
| `Targets` | Allowlisted names the cloud may use, each with an absolute `Path` and a `Description`. |
| `McpServers` | Local MCP servers started over STDIO. |
| `Capabilities` | Native tuning, plus `cli` and `mcp` capability entries. The first part of a name is its business domain. |
| `Domains` | Optional business descriptions of the domains (`Description`, `UseWhen`, `ExampleQuestions`); `Enabled: false` switches a whole domain off. |
| `Playbooks` | Optional multi-step business goals: ordered `Steps` of `Capability` and `Purpose`, followed by the cloud. |
| `MaxArtifactMegabytes` | Largest result one job may publish. Default 20. |

Relative paths resolve against the current directory, so use absolute paths outside development.

## Run during development

From the repository root. The Development config keeps job state in `src/AgentBridge/.local/data`; the Box folder is
the real one (Box Drive's `AgentBridge`, or `BoxRoot`). The self-test uses a simulated Box folder in `.local/selftest`.

```bash
dotnet run --project src/AgentBridge
```

```bash
dotnet run --project src/AgentBridge -- validate
```

```bash
dotnet run --project src/AgentBridge -- run agent.ping
```

```bash
dotnet test
```

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/selftest.ps1
```

`run` prints the response JSON on stdout and logs on stderr. In Windows PowerShell 5.1, escape the quotes inside
`--args`: `--args '{\"echo\":\"hi\"}'`.

## Run on another machine

```bash
dotnet publish src/AgentBridge -c Release -o publish
```

Copy `publish/` to the machine, put the machine's settings, if any, in `publish/appsettings.Local.json` (absolute
paths), then start `AgentBridge.exe` (or `dotnet AgentBridge.dll`). Only one worker can use a `LocalDataRoot` at a time.
To build from source on that machine instead, follow [docs/setup.md](docs/setup.md).

## Send a test request by hand

```powershell
$box = "C:\Users\<you>\Box\AgentBridge"
$id = [guid]::NewGuid().ToString()
'{"schemaVersion":1,"requestId":"' + $id + '","capability":"agent.ping"}' | Set-Content -Encoding utf8 "$box\inbox\$id.json"
```

The answer appears in `$box\responses\$id.json`.
