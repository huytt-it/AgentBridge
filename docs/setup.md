# Setting up AgentBridge on a machine

A runbook for an AI agent (or a person) setting the bridge up on a new Windows machine. Work through the steps
in order. Each step says what to run, what you should see, and what to do if you do not. Stop and ask the user
whenever a step says so; do not guess on their behalf.

Read [AGENTS.md](../AGENTS.md) first: its rules apply to every step here.

## Fast path

With no tools to connect yet, a machine needs no configuration at all:

1. Install **Box Drive** and sign in. It creates the folder `%USERPROFILE%\Box`.
2. From the repository root run `dotnet run --project src/AgentBridge`. The worker uses
   `%USERPROFILE%\Box\AgentBridge` (and creates it) as its Box folder, and the machine name as its id.
3. Paste the standing instruction from [cloud-agent.md](cloud-agent.md), step 2, into the cloud agent as it is:
   it already uses the folder name `AgentBridge`.

Some organisations do not allow creating folders at the root of Box. Then set `BoxRoot` in
`src/AgentBridge/appsettings.Local.json` to a folder they allow, and change the folder name in the cloud instruction
to match. The steps below do the same carefully, test every part and connect the tools.

## 0. Ask the user up front

Collect these answers before you start. You need them in steps 3 and 7.

1. Is **Box Drive** installed and signed in on this machine? By default the bridge uses the folder `AgentBridge` at
   the root of Box (`C:\Users\<them>\Box\AgentBridge`) and creates it. Does their organisation allow that, or must
   the bridge use another folder? Either way, their Microsoft 365 Copilot must be able to read and write it.
2. Should this machine have another name than its machine name in responses (`AgentId`), e.g. `dev-laptop-2`?
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

`True` means Box Drive's folder exists and the default BoxRoot, `%USERPROFILE%\Box\AgentBridge`, works. If it is
`False`, ask the user to install and sign in to Box Drive, or where it is mounted. Without Box Drive and without a
`BoxRoot` setting, the bridge refuses to start.

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

Skip this step when the defaults fit: Box Drive's `AgentBridge` folder, the machine name as `AgentId`, and no
targets yet (step 7 creates the file when you connect tools). Otherwise copy the template, then edit the copy:

```powershell
Copy-Item src/AgentBridge/appsettings.Local.example.json src/AgentBridge/appsettings.Local.json
```

In `src/AgentBridge/appsettings.Local.json` set:

- `AgentId`: only if the user chose a name in step 0. Default: the machine name.
- `BoxRoot`: only if the user chose another folder in step 0: its absolute path inside Box Drive.
  Default: `%USERPROFILE%\Box\AgentBridge`. The bridge creates the folder, and `inbox/`, `responses/`, `artifacts/`,
  `archive/` and `dead-letter/` inside it.
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
(`agent.ping` for now). Without a BoxRoot setting the line reads
`BoxRoot: C:\Users\<them>\Box\AgentBridge  (Box Drive default, because BoxRoot is not set)`. If you did set BoxRoot
and still see the default, the local file was not picked up: check its name and its JSON. Otherwise each error names
the setting to fix.

Under `dotnet run`, LocalDataRoot is `src\AgentBridge\.local\data`, from the Development settings. That is fine:
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
`catalog.json` and `README.md` appear in BoxRoot. They are what the cloud reads. The worker writes the same lines to
a file, `<LocalDataRoot>\logs\agentbridge-yyyyMMdd.log` (one per day); the first lines of a start name the folder.

From another terminal, drop a request the way the cloud would:

```powershell
$box = "<BoxRoot that validate printed in step 4>"
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

1. Take the standing instruction from its step 2 and give it to the user to paste into their Microsoft 365 /
   Copilot Studio agent. With the default BoxRoot it is ready as it is. If you set BoxRoot to another folder, first
   replace `AgentBridge` in it with that folder's name or path as the cloud agent sees it in Box.
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

A scheduled task shows no console, so the log file is where to look: `<LocalDataRoot>\logs\agentbridge-yyyyMMdd.log`.
Without a `LocalDataRoot` setting that is `%LOCALAPPDATA%\AgentBridge\logs` of the account that runs the task.

```powershell
Get-Content -Tail 50 "$env:LOCALAPPDATA\AgentBridge\logs\agentbridge-$(Get-Date -Format yyyyMMdd).log"
```

Checking, stopping, restarting and updating the task are in [operations.md](operations.md).

## 10. Updating later

```bash
git pull
```

Then rebuild, test and restart the worker. `appsettings.Local.json` is untouched by pulls.
[operations.md](operations.md#4-update-the-code) has the full sequence, including a published copy.

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
| `BoxRoot is not set and Box Drive was not found at ...` | Box Drive is not installed or signed in (step 1), or it is mounted elsewhere. Install it, or set BoxRoot in `appsettings.Local.json` (step 3). |
| BoxRoot shows the Box Drive default although you set it | The local file was not loaded: wrong name or folder, or invalid JSON. It must be `src/AgentBridge/appsettings.Local.json`. |
| Box does not let the bridge create `AgentBridge` at its root, or the folder never syncs | The organisation blocks folders at the Box root. Set BoxRoot to a folder they allow (step 3) and change the folder name in the cloud instruction (step 8). |
| `LocalDataRoot ... must be outside BoxRoot` | Move LocalDataRoot out of the Box folder; job logs must never sync. |
| `Another AgentBridge worker is already running` | Stop the other worker process (a terminal, or the scheduled task). |
| No response appears | Is the worker running and watching the right BoxRoot? Is Box Drive syncing? The file must end in `.json`, not `.json.txt`. Look in `dead-letter/` for `*.reason.txt`, and in the worker log, `<LocalDataRoot>\logs\agentbridge-yyyyMMdd.log` (search it for the requestId). More in [operations.md](operations.md). |
| Nothing on screen: the worker runs in the background | Read the log file: `<LocalDataRoot>\logs\agentbridge-yyyyMMdd.log`, e.g. `Get-Content -Tail 50 <file>`. If there is none, the worker cannot write to that folder: check `LocalDataRoot` and its permissions. |
| Response is `rejected` | `error.message` says what to fix. The request needs a new `requestId` to be sent again. |
| A request stays in `inbox/` | The file is not valid JSON yet (still syncing). After `IncompleteFileGraceSeconds` (default 60) it moves to `dead-letter/` with the parse error. |
| `run` with `--args` fails to parse in PowerShell 5.1 | Escape inner quotes: `--args '{\"echo\":\"hi\"}'`. |
| Build or test fails only with SDK 8 | Report the exact error to the user; do not change target frameworks or package versions. |
| `scripts/selftest.ps1` reports FAIL | Read `.local\selftest\logs\worker-*.err.log` and `.local\selftest\data\logs\agentbridge-*.log` and report the failing lines to the user. The self-test does not depend on Box Drive or on the real tools. |
| A CLI tool is refused as `.cmd`/`.bat` | Call the real program instead (`node x.js`, `python x.py`, `powershell -NoProfile -File x.ps1`); see onboarding-cli-tool.md. |
| An MCP server times out at startup | `npx` downloads on first run: raise `StartupTimeoutSeconds`, or run the server once by hand; see onboarding-mcp-server.md. |
