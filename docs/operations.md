# Operating a running AgentBridge

A runbook for an AI agent (or a person) that looks after a bridge that is already set up: check that it is healthy,
find what went wrong, restart it, update it, change what it offers and clean up old data. Setup is in
[setup.md](setup.md); read [AGENTS.md](../AGENTS.md) first, its rules apply here too.

Three habits keep this safe:

- **Look before you change.** Every section starts with commands that only read. Report what you found before you
  fix anything that changes what the cloud can do.
- **Ask the user first** for anything that changes the cloud's view (capabilities, domains, playbooks, targets),
  moves or deletes data, or restarts a worker that is in the middle of a job.
- **Logs and job folders may contain business data** (request purposes, tool output). They stay on this machine: do
  not paste them into an issue, a commit or a message to anyone but the user. The repository is public.

| You see | Go to |
|---|---|
| "Is it working?" | [1. Healthy](#1-healthy) |
| No response for a request | [1](#1-healthy), then [5](#5-handling-dead-letter) and [6](#6-investigating-a-response-that-is-not-succeeded) |
| `rejected`, `failed`, `timedOut`, `interrupted` | [6](#6-investigating-a-response-that-is-not-succeeded) |
| A file in `dead-letter/` | [5](#5-handling-dead-letter) |
| The user wants a tool added, changed or switched off | [7](#7-managing-capabilities-domains-and-playbooks) |
| Disk or Box folder is filling up | [8](#8-cleaning-up-old-data) |
| "Can it do X?" | [9](#9-known-limitations-of-this-version) |

## Notation

The commands below use two variables. Set them first and replace the placeholders.

```powershell
$box  = "<BoxRoot>"        # the folder shared with the cloud
$data = "<LocalDataRoot>"  # job state and logs, never synced
```

`validate` prints both (`BoxRoot:` and `LocalDataRoot:`), but it reports the settings of the process that runs it:

- `dotnet run --project src/AgentBridge -- validate` runs in the **Development** environment, whose `LocalDataRoot` is
  `src\AgentBridge\.local\data`.
- A published worker started by the scheduled task runs in **Production**: `LocalDataRoot` is
  `%LOCALAPPDATA%\AgentBridge` of the account running the task, unless `appsettings.Local.json` sets it.
  Run `C:\AgentBridge\AgentBridge.exe validate` (the published copy) to see what that worker uses.

The newest log file always settles it: the worker's first lines after a start say `Log files: <folder> ...` and
`... is watching <BoxRoot> ...`.

## 1. Healthy

A healthy bridge has all of these. Check them in this order; the first one that fails is where to look.

**1. The worker process is running.**

```powershell
Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'AgentBridge.exe' -or $_.CommandLine -match 'AgentBridge\.dll' } |
    Select-Object ProcessId, Name, CreationDate, CommandLine
```

Nothing listed means no worker. If it runs as the scheduled task, `schtasks /Query /TN AgentBridge /FO LIST /V`
shows `Status:` (Running, Ready) and `Last Result:`. Two workers on one `LocalDataRoot` cannot exist: the second exits
with `Another AgentBridge worker is already running`.

**2. The configuration is valid.**

```bash
dotnet run --project src/AgentBridge -- validate
```

Expect `Configuration is valid.`, the capabilities you expect, and no `Hints:`. This reads the same configuration
files a restart would read, so it also tells you whether the next start will succeed. It starts the configured MCP
servers briefly to read their tool lists.

**3. A local ping works.**

```bash
dotnet run --project src/AgentBridge -- run agent.ping
```

stdout is a JSON response with `"status": "succeeded"`. This runs the capability pipeline without Box and without the
running worker; it proves the build and the configuration, not the worker.

**4. A ping makes the round trip through Box.** This is the real test: the worker, the folder and Box Drive's sync
all take part. Dropping a test request into `inbox/` is allowed (AGENTS.md, rule 9).

```powershell
$id = "health-" + [guid]::NewGuid().ToString("N")
'{"schemaVersion":1,"requestId":"' + $id + '","capability":"agent.ping","purpose":"health check"}' | Set-Content -Encoding utf8 "$box\inbox\$id.json.tmp"
Rename-Item "$box\inbox\$id.json.tmp" "$id.json"   # the worker ignores .tmp, like a sync client finishing a file
$deadline = (Get-Date).AddSeconds(60)
while (-not (Test-Path "$box\responses\$id.json") -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
if (Test-Path "$box\responses\$id.json") {
    Get-Content -Raw -Encoding UTF8 "$box\responses\$id.json" | ConvertFrom-Json | Select-Object requestId, status, completedAtUtc, catalogVersion
} else { "No response within 60 seconds." }
```

Healthy: `status` is `succeeded` within a few seconds (the worker polls every `PollSeconds`, default 5) and the request
file is in `archive/`. The test leaves `archive/$id.json` and `responses/$id.json` behind; they are harmless and
[section 8](#8-cleaning-up-old-data) removes them with the rest.

**5. `catalog.json` is current.** The worker rewrites `catalog.json` and `README.md` at every start.

```powershell
Get-Content -Raw -Encoding UTF8 "$box\catalog.json" | ConvertFrom-Json | Select-Object agent, catalogVersion, generatedAtUtc
```

`generatedAtUtc` should be the time of the last worker start (UTC). An older value after a restart means the new
worker did not publish: read the log. `catalogVersion` is a hash of what the cloud may ask for (targets, domains,
playbooks, capabilities and their descriptions and arguments): it changes when that changes, not at every start.

**6. The log has no surprises.** The worker writes one file per local day, `agentbridge-yyyyMMdd.log`, in
`$data\logs`:

```powershell
Get-Content -Tail 50 "$data\logs\agentbridge-$(Get-Date -Format yyyyMMdd).log"        # a snapshot
Get-Content -Wait -Tail 50 "$data\logs\agentbridge-$(Get-Date -Format yyyyMMdd).log"  # follows it; Ctrl+C ends it
Select-String -Path "$data\logs\agentbridge-*.log" -Pattern ' (WARN|ERROR|CRIT) '      # every warning and error
```

(`-Wait` stays on the file it opened; after midnight open the new day's file.) A line looks like this, with the
exception on the lines after it:

```text
2026-11-02T10:15:30.123+07:00 INFO  AgentBridge.Processing.RequestProcessor: Request 0b6f3c1e agent.ping: Succeeded in 4 ms. Purpose: health check
```

A start writes `Log files: ...`, `Published catalog <version>` and `AgentBridge <version> is watching <BoxRoot>: ...`.
Each request writes `Request <id> <capability>: <Status> in <n> ms.` An idle worker writes nothing, so a quiet log
is normal; use the ping in step 4 to know that the worker is alive. `WARN` lines (dead-lettered file, interrupted job,
MCP server restarted) mean something needs a look; `ERROR` and `CRIT` mean something failed.

If no log file exists although the worker runs, it cannot write to `$data\logs` (the worker keeps running and prints
one line about it on stderr, which a scheduled task does not show). Check the folder's permissions and `LocalDataRoot`.

## 2. Where everything is

**Local data (`$data`, never synced)**

| Path | What |
|---|---|
| `logs\agentbridge-yyyyMMdd.log` | The worker's log, one file per local day. Files dated more than `LogRetentionDays` (default 14) days back are deleted when a day's file is first opened; nothing else in the folder is touched. Only the worker writes it: `validate`, `run` and `mcp-tools` log to stderr. |
| `jobs\<requestId>\` | One folder per request the worker accepted: `request.json` (the request as received), `meta.json` (its hash and claim time), `response.json` (the final response), `published.marker` (the response reached Box), `artifacts\` (files before they were copied to Box), and for CLI tools `stdout.log` and `stderr.log` (the tool's full, unmasked output; treat as sensitive). The folder also is the memory against running a request twice. |
| `local-runs\<id>\` | The working folders of the `run` command (local runs without Box): artifacts and, for CLI tools, `stdout.log` and `stderr.log`. |
| `staging\` | Files are written here and moved into Box when finished, so no half-written file is synced. Should be empty at rest. |
| `agentbridge.lock` | Held open by the running worker. |

**The Box folder (`$box`, synced with the cloud)**

| Path | What |
|---|---|
| `inbox\` | Requests from the cloud: `<requestId>.json`. The worker handles them oldest first, one at a time. |
| `responses\` | `<requestId>.json`, the answer to each request. |
| `artifacts\<requestId>\` | Larger results (Markdown, CSV...) that a response lists under `artifacts[].path`. |
| `archive\` | The requests that were handled (a repeated name gets a `-1`, `-2` suffix). |
| `dead-letter\` | Files that could not be used, each with a `<name>.reason.txt` (first line: UTC time, then the reason). |
| `catalog.json`, `README.md` | What the cloud reads to know what it may call and how. Written at every start. |

**Configuration**, later wins: `appsettings.json` (in git) → `appsettings.{Environment}.json` (`Development` under
`dotnet run`) → `appsettings.Local.json` (this machine; git ignores it) → environment variables
(`AgentBridge__BoxRoot`...) → command-line arguments. Files are read from the folder next to the program:
`src\AgentBridge\appsettings.Local.json` is copied to the build output by `dotnet run`; a published worker reads the
copy next to `AgentBridge.exe`. The worker reads all of it once, at start.

## 3. Start, stop, restart

The worker reads its configuration and builds its catalog only at start, so **every change needs a restart**.

**In a terminal** (the simplest setup): from the repository root run `dotnet run --project src/AgentBridge` (or
`C:\AgentBridge\AgentBridge.exe` for a published copy) and leave the terminal open. Stop it with Ctrl+C.

**As the scheduled task** from [setup.md, step 9](setup.md#9-keep-it-running-optional-ask-first):

```powershell
schtasks /Query /TN AgentBridge /FO LIST /V       # Status, Last Run Time, Last Result
schtasks /End /TN AgentBridge                     # stop it
schtasks /Run /TN AgentBridge                     # start it (a second worker would exit at once, see the lock)
```

Before you stop a worker, look at the end of today's log. Stopping while a job runs cancels the job: the cloud gets
an `interrupted` response and must send a new request. `schtasks /End` does not wait for the worker; it can also
leave MCP server processes behind, so if a server misbehaves after a restart, check for a stray one before blaming
the configuration.

After every start, run the checks in [section 1](#1-healthy), steps 1, 4 and 5. If the worker exits at once, run it in
a terminal: the reason is on screen (`Configuration is invalid:`, `Another AgentBridge worker is already running`,
BoxRoot not reachable...).

## 4. Update the code

From the repository root, on `main`, with the user's agreement (an update changes what runs on their machine):

```bash
git status
```

```bash
git pull
```

```bash
dotnet build AgentBridge.sln
```

```bash
dotnet test AgentBridge.sln
```

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/selftest.ps1
```

Expect 0 warnings, 0 errors, every test passing and `ALL PASSED`. `appsettings.Local.json` is not in git and a pull
does not touch it. If the worker runs through `dotnet run` from this checkout, stop it before building: the running
program locks its files. The self-test runs in `.local\selftest` with its own worker and does not disturb the real
one.

Then read what changed (`git log --stat` for the pulled commits) for configuration or contract changes, and
restart:

- **Terminal worker:** stop it, start it again.
- **Published worker:** stop the task, publish, compare the settings, start the task.

  ```powershell
  schtasks /End /TN AgentBridge
  dotnet publish src/AgentBridge -c Release -o C:\AgentBridge
  fc /b src\AgentBridge\appsettings.Local.json C:\AgentBridge\appsettings.Local.json
  schtasks /Run /TN AgentBridge
  ```

  Publish copies `appsettings.Local.json` from the source folder into `C:\AgentBridge`, but it may skip a copy that is
  newer there. `fc` shows whether the two files are the same. If someone edited the copy in `C:\AgentBridge`
  directly, decide with the user which one is right before starting.

Finish with the health checks. `catalogVersion` may change after an update when the update changed what the catalog
says (a description, a new built-in capability); that is expected, and the cloud re-reads `catalog.json` when it sees
a new version.

## 5. Handling `dead-letter/`

`dead-letter\` holds request files the worker could not use. Each has a `.reason.txt` beside it that says why and
what the cloud should do.

```powershell
Get-ChildItem "$box\dead-letter" -Filter *.reason.txt | Sort-Object LastWriteTime |
    ForEach-Object { "== $($_.Name)"; Get-Content -Encoding UTF8 $_.FullName }
```

Each move also leaves a `WARN ... Moved <file> to dead-letter/: <reason>` line in the log.

| Reason starts with | What happened | What to do |
|---|---|---|
| `The file is not a valid request: ...` | For longer than `IncompleteFileGraceSeconds` (default 60 s) the file was not a JSON object with `schemaVersion`, `requestId` and `capability`: the cloud wrote bad JSON, or the sync is very slow. | Open the file. Broken JSON: the cloud must send a new request; tell the user if it keeps happening. Complete, valid JSON now (a late sync): ask the user whether to move it back. |
| `requestId '...' cannot be used: ...` | The id is empty, longer than 128 characters, uses other characters than letters, digits, `.`, `_`, `-`, is a Windows device name such as `CON`, or looks like a path (`..\x`). | The cloud must send a new request with a valid id (a fresh GUID). Moving the file back only dead-letters it again. An id that looks like a path trick: tell the user, the file is untrusted data. |
| `duplicate_conflict: requestId X was already used for a different request ...` | The cloud reused an id for other content. The original job is untouched; compare with `responses\X.json`. | **Never move it back.** The cloud must send a new request with a new requestId. |

**Never move a file back into `inbox/` without the user's yes, and never one with `duplicate_conflict`.** When the
user agrees, move (not copy) only the request file, with its original name. Leave the `.reason.txt` where it is.
Files are not removed automatically; [section 8](#8-cleaning-up-old-data) cleans up.

A file that stays in `inbox/` is not dead-lettered yet: it may still be syncing, or the worker failed to process it
(`ERROR ... Could not process <file>; it stays in inbox/ and is retried on the next poll`, repeated at every poll:
look at the exception, usually permissions or a full disk).

## 6. Investigating a response that is not `succeeded`

Read the response first; `error.code` and `error.message` are written to be acted on.

```powershell
Get-Content -Raw -Encoding UTF8 "$box\responses\<requestId>.json" | ConvertFrom-Json | Format-List status, capability, startedAtUtc, completedAtUtc, error, catalogVersion
Select-String -Path "$data\logs\agentbridge-*.log" -Pattern '<requestId>' -Context 0,3
Get-ChildItem "$data\jobs\<requestId>"
```

| Status and `error.code` | Meaning | Look at, do |
|---|---|---|
| `rejected`: `invalid_request`, `unsupported_schema_version`, `unknown_capability`, `target_required`, `unknown_target`, `invalid_args`, `expired` | The request was not run; the cloud sent something this agent does not accept. | The message lists what is accepted. Usually only the cloud's request is wrong and it must send a new one. If the cloud asks for a capability or target that **should** exist, compare `validate` and `catalog.json`: it may be disabled, or the worker was not restarted after a change. `expired`: the message shows the agent's clock; compare the machine's time. |
| `failed`: `execution_failed` | The tool ran and failed. | CLI: `jobs\<id>\stderr.log` and `stdout.log` and the log line `<capability>: the tool exited with code N`. MCP: the message holds the tool's error; the log has the server's stderr lines (category `AgentBridge.Mcp.ServerStderr`) and `MCP server '...' stopped...` warnings. Run the same thing by hand with `run <capability> --args ...`. |
| `failed`: `artifact_too_large` | The result was bigger than `MaxArtifactMegabytes` (default 20); nothing was published. | The cloud should ask for less. Do not raise the limit without asking the user. |
| `failed`: `internal_error` | A bug or an unexpected exception. | The log has `ERROR ... failed with an unexpected error` with the stack trace. Report it to the user; retrying blindly will not help. |
| `timedOut`: `timeout` | The capability did not finish within its `TimeoutSeconds` (CLI default 60). A CLI tool's whole process tree was killed. | Is the tool slow, or waiting for input, a login or a lock? Time it by hand. Raising `TimeoutSeconds` is a configuration change: ask the user ([section 7](#7-managing-capabilities-domains-and-playbooks)). For an MCP tool see [limitations](#9-known-limitations-of-this-version). |
| `interrupted`: `interrupted` | The worker stopped while the job ran (restart, crash, `schtasks /End`, power loss). It was **not** re-run, because it may have had side effects. | Find why the worker stopped: the log just before the gap, and Windows' own events. The cloud must send a new request. |

No response at all: is the worker running (section 1, step 1)? Is the request file still in `inbox/`, in
`dead-letter/` or in `archive/`? Does the log mention its id? If `responses\<id>.json` exists on this machine but the
cloud does not see it, the problem is Box's sync (look at Box Drive's status), not the bridge. If the request file
never reached this machine, the cloud's write or Box's sync failed.

## 7. Managing capabilities, domains and playbooks

Everything the cloud can do is in `appsettings.Local.json` (in `src\AgentBridge`; the published copy for a published
worker). **Always ask the user before you add, change, disable or remove anything**: the entry is what authorizes the
cloud to call it. Show the exact entry you intend to write. Read-only tools only, no command runners, nothing
local in the texts the cloud sees (AGENTS.md, rules 1 to 3 and 5).

| Task | How | Guide |
|---|---|---|
| Add a command-line tool | A `Type: "cli"` entry under `AgentBridge:Capabilities` | [onboarding-cli-tool.md](onboarding-cli-tool.md) |
| Add a local MCP server and its tools | An entry under `AgentBridge:McpServers`, then a `Type: "mcp"` entry per tool | [onboarding-mcp-server.md](onboarding-mcp-server.md) |
| Add a native .NET capability | A class implementing `ICapability` and one line in `ServiceCollectionExtensions` | [adding-capabilities.md](adding-capabilities.md) |
| Describe a domain, add a playbook | `AgentBridge:Domains:{name}`, `AgentBridge:Playbooks:{name}` | [architecture.md](architecture.md#domains-playbooks-and-access), and the "Describe the domain" and "Consider a playbook" parts of the two onboarding guides |
| Add or change a target (a folder the cloud may name) | `AgentBridge:Targets:{name}` with an absolute `Path` and a `Description` | [setup.md, step 3](setup.md#3-configure-this-machine) |
| Edit a capability (description, arguments, timeout) | Change its entry; the field reference is in the onboarding guide of its type | The same guide |
| Switch one off for a while | `"Enabled": false` on the capability, its domain, its playbook or its MCP server. A disabled domain or server is never started | The same guide; [architecture.md](architecture.md#domains-playbooks-and-access) |
| Remove one | Delete its entry, and any playbook step that names it: a step that names an unknown capability is a startup error | The same guide |

After any change:

1. `dotnet run --project src/AgentBridge -- validate` (and `run <capability>` for a new or edited one) must succeed
   before you restart.
2. Restart the worker ([section 3](#3-start-stop-restart)). Nothing is hot-reloaded.
3. Open `catalog.json`: the capability, domain or playbook is there or gone as intended, `generatedAtUtc` is the time of
   the restart, and `catalogVersion` has **changed**. Every response carries `catalogVersion`; when the cloud sees a new
   one it reads `catalog.json` again. If a change the cloud can see (a name, a description, an argument, a target)
   left `catalogVersion` as it was, the edit was not loaded: wrong file, or no restart. A change the cloud cannot see,
   such as a `TimeoutSeconds`, can leave it unchanged; `generatedAtUtc` and the log then show that the restart happened.
4. Tell the user what changed, what the cloud can now do and anything you could not verify.

## 8. Cleaning up old data

Nothing deletes old data except the log retention: `archive/`, `responses/`, `artifacts/`, `dead-letter/` in Box and
the `jobs/` and `local-runs/` folders on this machine grow without limit. Clean up by hand, **only with the user's
yes**, and look before you delete: first list, show the user, then remove.

Things to know before you do:

- **Removing a `jobs\<requestId>` folder lets that requestId be accepted again** (the folder is how the worker knows
  it ran the request). Keep the job folders of anything the cloud might still resend; keep them longer than the other
  data.
- Leave job folders without `published.marker` alone: they belong to a request that has no response in Box yet, and the
  worker reports it as `interrupted` at the next start. The script below skips them.
- Do not remove a response the cloud may not have read yet. Choose an age in days that is longer than any request
  can take (a week or more).
- Deleting inside Box follows **Box's own rules**: deleted items go to Box's trash and are kept or purged under
  Box's trash and retention settings, not under any setting of the bridge. The deletion also reaches everyone who can
  see the folder, including the cloud agent.

Step 1, list (changes nothing). Set `$days` to the age you agreed on:

```powershell
$days   = 30
$cutoff = (Get-Date).AddDays(-$days)
$old = @()
foreach ($name in "archive", "responses", "artifacts", "dead-letter") {
    $old += @(Get-ChildItem -LiteralPath "$box\$name" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt $cutoff })
}
# Finished jobs only (published.marker exists) and old local runs.
$old += @(Get-ChildItem -LiteralPath "$data\jobs" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -lt $cutoff -and (Test-Path -LiteralPath (Join-Path $_.FullName "published.marker")) })
$old += @(Get-ChildItem -LiteralPath "$data\local-runs" -Directory -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt $cutoff })
$old | Sort-Object FullName | Select-Object FullName, LastWriteTime, Mode | Format-Table -AutoSize
"{0} items older than {1} days." -f $old.Count, $days
```

Show the user the list (and the counts per folder). Unfinished jobs and anything younger than the cutoff are not in it.

Step 2, remove, after their yes. `-WhatIf` repeats the list once more without deleting; drop it to delete:

```powershell
$old | Remove-Item -Recurse -Force -WhatIf
```

Afterwards run the health ping ([section 1](#1-healthy), step 4) to confirm the worker is unaffected.

## 9. Known limitations of this version

Each of these is true of the code today; do not promise more to the user or the cloud.

- **No hot reload.** Configuration and the catalog are read once, at start. Every change needs a restart.
- **Read access only, and no approval step.** `Access` is `read` for every capability; startup refuses anything else on
  an enabled capability. Nothing that writes, deletes, pushes, sends or deploys can be connected yet.
- **Jobs run one at a time.** A long job (up to its `TimeoutSeconds`) delays every request behind it, including a health
  ping. Requests wait in `inbox/` and are not lost, but one with an `expiresAtUtc` may expire while it waits.
- **A timed-out MCP call is not cancelled in the server.** The bridge stops waiting and answers `timedOut`, but the MCP
  SDK in use (ModelContextProtocol.Core 2.3.0) sends no cancellation, so the server may keep working on the call and be
  slow for the next ones. Restarting the worker restarts the server. A timed-out CLI tool is different: its process tree
  is killed.
- **No automatic cleanup of Box or job data.** Only the worker's own log files expire (`LogRetentionDays`). See
  [section 8](#8-cleaning-up-old-data).
- **Box is reached only through a synced folder** (Box Drive or another sync client). There is no Box API transport; if
  the sync stops, the bridge stops hearing from the cloud and the cloud stops hearing from it, and nothing on this
  side reports it.
- **No Copilot CLI capability yet.** The capabilities that exist are native .NET tools, command-line tools and tools of
  local MCP servers.
- **No request signing.** Anyone who can write to `inbox/` can send requests. The worker validates each one and only
  read-only capabilities exist, but share the folder as narrowly as you can.
- **One worker per `LocalDataRoot`.** A second one exits at once with `Another AgentBridge worker is already running`.
- **Without Box Drive, `validate` and the worker fail unless `BoxRoot` is set.** The default folder is
  `%USERPROFILE%\Box\AgentBridge`.
- **The file log belongs to the worker only.** `validate`, `run` and `mcp-tools` write to stderr. A day's log file stays
  open while the worker runs, so it cannot be deleted or renamed until the worker stops; read it with `Get-Content`,
  which shares it.
