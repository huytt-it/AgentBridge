# Setting up AgentBridge on a machine

A runbook for an AI agent (or a person) setting the bridge up on a new Windows machine. Work through the steps
in order. Each step says what to run, what you should see, and what to do if you do not. Stop and ask the user
whenever a step says so; do not guess on their behalf.

Read [AGENTS.md](../AGENTS.md) first: its rules apply to every step here.

## 0. Ask the user up front

Collect these answers before you start. You need them in steps 3 and 7.

1. Is **Box Drive** installed and signed in on this machine? Which folder in Box should the bridge use? It must be
   a folder that their Microsoft 365 Copilot can read and write, e.g. `C:\Users\<them>\Box\AgentBridge`.
2. What should this machine be called in responses (`AgentId`), e.g. `dev-laptop-2`?
3. Which **local repositories or folders** may the cloud refer to as targets? Give each a short business
   description.
4. Which **tools** should be connected, and where are they? For example: the backlog exporter (a command-line
   tool) and the code-map MCP server. Ask for the command they normally use to run each one.
5. May you install the .NET SDK if it is missing?

## 1. Check the prerequisites

```bash
dotnet --list-sdks
```

You need an SDK whose version starts with `8.0` or `10.0`. Either works: the app targets .NET 8 and runs on .NET 10
too. If neither is installed, ask the user to install one, for example with
`winget install Microsoft.DotNet.SDK.10`. Do not install anything without their yes.

```bash
git --version
```

```powershell
Test-Path "$env:USERPROFILE\Box"
```

`True` means Box Drive's default folder exists. If it is `False`, ask the user where Box Drive is mounted, or
whether it is installed at all. Without Box the bridge still runs, but the cloud cannot reach it.

## 2. Build and test

```bash
dotnet build AgentBridge.sln
```

Expect `Build succeeded` with 0 warnings and 0 errors.

```bash
dotnet test AgentBridge.sln
```

Every test must pass. With a .NET 10 SDK the tests run twice, on net8.0 and net10.0. With a .NET 8 SDK they run on
net8.0 only; that is expected.

If the build fails with a .NET 8 SDK, record the exact error and report it to the user. Do not change target
frameworks or package versions to make it pass.

Then run the end-to-end self-test:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/selftest.ps1
```

It starts the real worker against a simulated Box folder in `.local\selftest`. Stand-in tools play a backlog
exporter and an MCP server. Nothing outside `.local\selftest` is touched: not your configuration, not the real Box
folder, not a worker that is already running.

Every line must say PASS and the last line `ALL PASSED`. To also check .NET 10 when it is installed, run it again
with `-FxVersion <version>`, using a `Microsoft.NETCore.App 10.x` version from `dotnet --list-runtimes`.
If a check fails, the worker logs are in `.local\selftest\logs`.

## 3. Configure this machine

Copy the template, then edit the copy:

```powershell
Copy-Item src/AgentBridge/appsettings.Local.example.json src/AgentBridge/appsettings.Local.json
```

In `src/AgentBridge/appsettings.Local.json` set:

- `AgentId`: the name from step 0.
- `BoxRoot`: the absolute path of the Box folder from step 0. Create the folder if it does not exist yet.
  The bridge creates `inbox/`, `responses/`, `artifacts/`, `archive/` and `dead-letter/` inside it.
- `Targets`: one entry per folder from step 0, each with an absolute `Path` and a `Description`.
  The description is what the cloud sees: no local paths in it.

Leave `McpServers` and `Capabilities` empty for now; step 7 fills them.

Never edit `appsettings.json` for machine settings, and never commit `appsettings.Local.json` (git ignores it).
Settings load in this order, later ones winning: `appsettings.json`, `appsettings.{Environment}.json`,
`appsettings.Local.json`, environment variables (`AgentBridge__BoxRoot`, ...), command-line arguments.

## 4. Validate

```bash
dotnet run --project src/AgentBridge -- validate
```

Expect `Configuration is valid.` followed by BoxRoot, LocalDataRoot, the target names and the capability list
(`agent.ping` for now). Check that BoxRoot is the Box folder and not `.local\box\...`. If it is `.local`, the local
file was not picked up: check its name and its JSON. Otherwise each error names the setting to fix.

Under `dotnet run`, LocalDataRoot is `.local\data` in the repository, from the Development settings. That is fine:
git ignores it. The published app (step 9) uses `%LOCALAPPDATA%\AgentBridge` unless you set LocalDataRoot.

## 5. Smoke test without Box

```bash
dotnet run --project src/AgentBridge -- run agent.ping
```

stdout is a JSON response with `"status": "succeeded"`. Logs go to stderr.

## 6. Run the worker and test through Box

Start the worker in its own terminal and leave it running:

```bash
dotnet run --project src/AgentBridge
```

Expect a log line `AgentBridge ... is watching <BoxRoot>: catalog <version>, N capabilities ...`.
`catalog.json` and `README.md` appear in BoxRoot. They are what the cloud reads.

From another terminal, drop a request the way the cloud would:

```powershell
$box = "<BoxRoot from step 3>"
$id = [guid]::NewGuid().ToString()
'{"schemaVersion":1,"requestId":"' + $id + '","capability":"agent.ping"}' | Set-Content -Encoding utf8 "$box\inbox\$id.json"
```

Within a few seconds, `$box\responses\$id.json` holds `"status": "succeeded"` and the request moves to
`$box\archive\`. The cloud side is connected in step 8.

Stop the worker (Ctrl+C) before step 7; it reads its configuration only at start.

## 7. Connect the tools

For each tool from step 0, follow the matching guide. Each one ends with `validate` and a local `run`.

- Command-line tools: [onboarding-cli-tool.md](onboarding-cli-tool.md)
- MCP servers: [onboarding-mcp-server.md](onboarding-mcp-server.md)

Then start the worker again and check that `catalog.json` lists the new capabilities.

## 8. Connect Microsoft 365 Copilot (the user does this)

The cloud agent learns what it can call from `README.md` and `catalog.json` in the Box folder, but it has to be
told once that the folder exists. Follow [cloud-agent.md](cloud-agent.md):

1. Take the standing instruction from its step 2, replace `AgentBridge` with this machine's Box folder name, and give
   the finished text to the user to paste into their Microsoft 365 / Copilot Studio agent.
2. Ask the user to make sure the agent can read and create files in that folder (its step 1).
3. With the worker running, ask the user to try the test prompts from its step 3. Watch the worker log and
   `responses/` while they do, and use its troubleshooting table if nothing arrives.

## 9. Keep it running (optional, ask first)

The simplest setup is a terminal running `dotnet run --project src/AgentBridge`. To start it at sign-in instead,
publish it and register a scheduled task. This changes the user's system, so ask before you do it.

```bash
dotnet publish src/AgentBridge -c Release -o C:\AgentBridge
```

`appsettings.Local.json` is published along with the app. Re-publish after changing it, or edit the copy in
`C:\AgentBridge`. Use absolute paths only: a scheduled task does not start in the repository folder.

```powershell
schtasks /Create /SC ONLOGON /TN AgentBridge /TR "C:\AgentBridge\AgentBridge.exe" /RL LIMITED
```

Only one worker may use a LocalDataRoot at a time. A second one exits with
`Another AgentBridge worker is already running`.

## 10. Updating later

```bash
git pull
```

Then rebuild, test and restart the worker. `appsettings.Local.json` is untouched by pulls.

## 11. Report back to the user

Tell them:

- the SDK version used;
- the build and test results;
- BoxRoot, AgentId and the targets you configured;
- every capability you enabled, with its description;
- whether the round trip through Box worked (step 6) and whether the cloud agent's test prompts worked (step 8);
- the finished standing instruction you prepared for the cloud agent;
- anything you skipped or could not verify.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `AgentBridge:BoxRoot is required` | `appsettings.Local.json` is missing or has no BoxRoot (step 3). |
| BoxRoot shows `.local\box\AgentBridge` | The local file was not loaded: wrong name or folder, or invalid JSON. It must be `src/AgentBridge/appsettings.Local.json`. |
| `LocalDataRoot ... must be outside BoxRoot` | Move LocalDataRoot out of the Box folder; job logs must never sync. |
| `Another AgentBridge worker is already running` | Stop the other worker process (a terminal, or the scheduled task). |
| No response appears | Is the worker running and watching the right BoxRoot? Is Box Drive syncing? The file must end in `.json`, not `.json.txt`. Look in `dead-letter/` for `*.reason.txt`. |
| Response is `rejected` | `error.message` says what to fix. The request needs a new `requestId` to be sent again. |
| A request stays in `inbox/` | The file is not valid JSON yet (still syncing). After `IncompleteFileGraceSeconds` (default 60) it moves to `dead-letter/` with the parse error. |
| `run` with `--args` fails to parse in PowerShell 5.1 | Escape inner quotes: `--args '{\"echo\":\"hi\"}'`. |
| Build or test fails only with SDK 8 | Report the exact error to the user; do not change target frameworks or package versions. |
| `scripts/selftest.ps1` reports FAIL | Read `.local\selftest\logs\worker-*.err.log` and report the failing lines to the user. The self-test does not depend on Box Drive or on the real tools. |
| A CLI tool is refused as `.cmd`/`.bat` | Call the real program instead (`node x.js`, `python x.py`, `powershell -NoProfile -File x.ps1`); see onboarding-cli-tool.md. |
| An MCP server times out at startup | `npx` downloads on first run: raise `StartupTimeoutSeconds`, or run the server once by hand; see onboarding-mcp-server.md. |
