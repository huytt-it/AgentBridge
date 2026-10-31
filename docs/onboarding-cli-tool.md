# Onboarding a command-line tool

How to give the cloud agent (Microsoft 365 Copilot) a local command-line tool, using **configuration only**: no C#,
no rebuild. Written for an AI assistant that sets up a real tool on a machine it has never seen, with the user
in the loop. Read [architecture.md](architecture.md) once for the big picture; this page is the whole procedure.

## What you are building

A `Type: "cli"` entry under `AgentBridge:Capabilities` in **`src/AgentBridge/appsettings.Local.json`**. The worker
turns the entry into a named capability. When the cloud sends a request such as

```json
{ "schemaVersion": 1, "requestId": "r-001", "capability": "backlog.export", "args": { "project": "DEMO" } }
```

the worker starts the configured program with the configured arguments (the request's values filled into
placeholders), waits for it, and writes a JSON response plus any result files (Markdown, CSV) back to Box.

What the cloud can **never** do: choose the program, add arguments, send a path, or send a command line. It can only
fill in the values of the params you declare, and every value is checked against a pattern before the tool starts.
Your job is to declare as little as possible and as tight as possible.

### Rules you must follow

1. **Read-only tools only.** Nothing that writes, deletes, pushes, deploys, sends or changes a database or a
   configuration. If the tool can do both, expose only the read operation (fixed in `Args`) and leave the rest
   unreachable. Anything with side effects stays disabled until a local approval step exists
   ([architecture.md](architecture.md), rule 6).
2. **The user decides.** Do not expose a tool because it exists. Propose the exact entry and wait for a clear yes
   (step 4).
3. **No secrets in git.** `appsettings.json` is committed. Put tokens only in `appsettings.Local.json`
   (git ignores it) under the entry's `Environment`, or in the user's own environment variables.
4. **No local details in what the cloud reads.** `Description`, `UseWhen`, param descriptions and examples are
   published in `catalog.json`: no paths, executable names, command lines, user names, machine names or credentials.
5. **Never rename a capability** once the cloud may use it; add a new one instead.

## Before you start

- The repository is cloned and `dotnet build AgentBridge.sln` works (.NET SDK 8 or 10).
- `AgentBridge:BoxRoot` is set for this machine (README, "Configure"). `dotnet run --project src/AgentBridge -- validate`
  must already succeed *without* your new entry. If it does not, fix that first; it is not part of this task.
- You know which language the cloud agent works in. `Description` and `UseWhen` are read by it; ask the user whether
  they should be English or Vietnamese.

## Step 1. Find the tool and learn how it behaves

1. If you do not know where the tool is, **ask the user**: where it is installed or its folder, and how they run it
   today. Do not search the disk for candidates and pick one.
2. Run its help and read all of it, including subcommand help:

   ```bash
   <tool> --help
   <tool> <subcommand> --help
   ```

3. Write down, for the operation you will expose:
   - the program to start and how: a native `.exe`, or an interpreter plus a script (`node script.js`,
     `python script.py`, `powershell -NoProfile -File script.ps1`);
   - every argument it needs, which are required, what values are valid;
   - **where the result goes**: stdout, or a file you name with an option such as `--out <file>`;
   - the result format: Markdown, CSV, JSON or plain text;
   - whether it needs credentials (environment variable, config file, a prior login) and whether it ever prompts
     for input (the bridge closes stdin, so a prompt means the tool fails);
   - how long a normal run takes;
   - the exit code on failure and where it writes error messages (stderr is best).
4. Run it once by hand with real arguments and look at the output. Check that Vietnamese text is intact; the bridge
   reads stdout/stderr as **UTF-8** (a PowerShell script may need `[Console]::OutputEncoding = [Text.Encoding]::UTF8`).

## Step 2. Decide what is safe to expose

Expose an operation only if all of these are true:

- [ ] It only **reads**. It does not write to the source system, delete, update, push, send, deploy or migrate.
- [ ] Its only output is the result it prints or the one file the bridge names for it. (Writing a cache or a log is
      usually fine; if it writes anywhere that matters, ask the user.)
- [ ] The values the cloud will supply cannot change what it does, only what it reads (a project key, a status,
      a date). A parameter that is a path, a URL, a query language, a command or a free-form flag is **not** safe.
- [ ] A wrong or hostile value can at worst return nothing useful.

If the tool has write operations, say so to the user and leave them out. If you are unsure, ask.

## Step 3. Design the capability

Decide each of these before writing JSON.

**Name.** Lowercase dotted segments, business-oriented: `backlog.export`, `code.business-rules`. Not `run-tool`,
not the executable's name. Pattern: `^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)+$`.

**`Description`** (required): what the capability *returns*, in the words of the business analyst who reads it, one or
two sentences. Good: "Exports the open backlog items of a project as Markdown, grouped by epic, with status and
priority." Bad: "Runs backlog-cli.exe export --format md."

**`UseWhen`**: the situation in which the cloud should pick it, or when it should not. "When you need the current
scope of a project before analysing requirements."

**Params** (what the cloud may send). For each: camelCase name, `Type` (`string`, `integer`, `number`, `boolean`),
`Description`, `Required`, and for strings a tight `Pattern`. Rules of thumb:

Patterns as they are written **in the JSON file** (every backslash is doubled there):

- a project key such as `DEMO`: `"Pattern": "[A-Z][A-Z0-9]{1,9}"`
- one of a fixed list: `"Pattern": "Open|InProgress|Done"`
- an ISO date: `"Pattern": "\\d{4}-\\d{2}-\\d{2}"`
- a short search term: leave `Pattern` out (the default below), or `"Pattern": "[\\p{L}\\p{N} ._-]{1,100}"`

The pattern must match the **whole** value. If you leave `Pattern` out, a string param gets the safe default
`^(?!-)[^\r\n]{0,500}\z`: no leading `-` (so a value can never be read as an option), no line breaks, at most 500
characters. A pattern you write yourself is not given that protection automatically; startup refuses a pattern that
allows a leading `-` when an argument starts with the value (see "Placeholders").

**Output** (what the tool produces):

| `Output` | Use for | What the cloud gets |
|---|---|---|
| `markdown` | A report or listing for reading | `artifacts/<requestId>/result.md` |
| `csv` | Tabular data | `artifacts/<requestId>/result.csv` |
| `json` | Small structured data | `result` holds the JSON (up to 256 KB; larger becomes `result.json`) |
| `text` | A short answer | `result` = `{ exitCode, output, truncated }` (last 32 KB of stdout) |

Prefer `markdown`/`csv` for anything long: large results belong in files, not in the response.

**Timeout.** `TimeoutSeconds` (default 60) must exceed a normal run with margin, but not by much. When it expires the
whole process tree is killed and the cloud gets status `timedOut`.

**Target.** If the tool works on a local folder (a repository, a project export), do not take a path as a param.
Set `RequiresTarget: true`, add the folder under `AgentBridge:Targets` (name, absolute `Path`, a `Description` without
paths), and use `{targetPath}` in `Args`. The tool then also runs with that folder as its working directory.

## Step 4. Propose the entry to the user and wait

Show the user the complete JSON entry and a plain-language summary, then **stop and wait for approval**. For example:

> I would like to expose this tool to the cloud agent as `backlog.export`. It runs the exporter read-only for one
> project (and optionally one status) and returns the result as a Markdown file. The cloud can only send `project`
> (2 to 10 capital letters or digits) and `status` (Open, InProgress or Done); it cannot change anything else. The
> API token stays in `appsettings.Local.json` on this machine. May I add it? (entry below)

Do not continue on silence or on an answer that does not clearly approve this entry. Apply changes the user asks for
and show the revised entry.

## Step 5. Write the entry into `appsettings.Local.json`

File: `src/AgentBridge/appsettings.Local.json` (next to `appsettings.json`; git ignores it). If it exists, **merge**
into it; never replace other settings (`BoxRoot`, `Targets`, other capabilities). If it does not exist, create it:

```json
{
  "AgentBridge": {
    "Capabilities": {
      "<capability.name>": { "Type": "cli", "...": "..." }
    }
  }
}
```

- JSON needs `\\` for a backslash in paths. Forward slashes work on Windows and avoid mistakes:
  `"C:/Tools/backlog/export.js"`.
- Tokens and passwords go in the entry's `Environment` (below) or in the user's environment variables. Never in
  `appsettings.json`, never in `Args`, never in a description.
- Keep the entry minimal: only the arguments the operation needs.

## Step 6. Validate, then run it for real

From the repository root:

```bash
dotnet run --project src/AgentBridge -- validate
```

`validate` checks every entry at once and lists **every** problem, each starting with the config key to change
(e.g. `AgentBridge:Capabilities:backlog.export:Exe '...' was not found on PATH`). Fix them all and run it again until
it prints `Configuration is valid.` and lists your capability. Then run the capability as the cloud would, with real
values:

```bash
dotnet run --project src/AgentBridge -- run backlog.export --args '{"project":"DEMO","status":"Open"}'
```

In **Windows PowerShell 5.1** escape the quotes inside the JSON:

```powershell
dotnet run --project src/AgentBridge -- run backlog.export --args '{\"project\":\"DEMO\",\"status\":\"Open\"}'
```

(Add `--target <name>` when the capability has `RequiresTarget`.) `run` executes the capability locally, without Box,
and prints the response JSON on stdout. Logs and the line `Artifact (local only, not published): <path>` go to
stderr. Check:

- [ ] `"status": "succeeded"` and a sensible `summary`.
- [ ] Open the artifact path it printed (for `markdown`/`csv`): is the content what an analyst needs? Is Vietnamese
      text intact? Is there anything that should not leave the machine (secrets, internal URLs, personal data the
      user did not mean to share)?
- [ ] A bad request is rejected without running the tool, e.g. `--args '{"project":"demo"}'` (breaks the pattern),
      an unknown arg, a missing required arg. Each gives `"status": "rejected"` with a message that says what is
      accepted.
- [ ] The tool's own failure is reported: if you can, force one (an unknown project) and read the `error.message`.

The full, unmasked output of the tool is kept locally in `stdout.log` and `stderr.log` in the run's folder
(`<LocalDataRoot>/local-runs/<id>/` for `run`, `<LocalDataRoot>/jobs/<requestId>/` for real requests; in Development
`LocalDataRoot` is `src/AgentBridge/.local/data`). Look there when something fails.

## Step 7. Restart the worker and confirm the catalog

`catalog.json` is written once, at startup. Restart the worker (stop it with Ctrl+C, then):

```bash
dotnet run --project src/AgentBridge
```

Then open `catalog.json` in the Box folder (`BoxRoot`) and confirm the capability is listed with the description,
params and example you wrote. Tell the user it is live and give them one example request body from the catalog.
(A published build reads `appsettings.Local.json` from the folder next to `AgentBridge.exe`; copy the file there.)

---

## Field reference

Keys are case-insensitive. Fields not listed for `cli` (`Server`, `Tool`, `FixedArgs`) are for MCP entries.

| Field | Required | Default | Meaning |
|---|---|---|---|
| `Type` | yes | `native` | Must be `"cli"`. |
| `Enabled` | no | `true` | `false` removes the capability (nothing is started or checked). |
| `Description` | **yes** | | What the capability returns, in business terms. Published. |
| `UseWhen` | no | | When the cloud should choose it. Published. |
| `Exe` | **yes** | | The program. Either a bare name found on `PATH` (`node`, `python`, `powershell`, `mytool`; `PATHEXT` is honoured on Windows) or an **absolute** path that exists. Resolved to a full path at startup. `.bat` and `.cmd` are refused. A relative path with folders (`.\tool.exe`) is refused. |
| `Args` | no | `[]` | Argument template. **Each item is exactly one argument.** See "Placeholders". |
| `OptionalArgs` | no | `{}` | `{ "paramName": [ items... ] }`. The items are added only when the param is sent (for a boolean: when it is `true`). |
| `Params` | no | `{}` | The args the cloud may send, keyed by camelCase name. See the next table. |
| `Output` | no | `text` | `markdown`, `csv`, `json` or `text`. |
| `RequiresTarget` | no | `false` | The request must name a configured target. Enables `{targetPath}` and sets the working directory. |
| `WorkingDirectory` | no | job folder | Folder to run in when there is no target. Must exist. Relative paths resolve against the current directory. With `RequiresTarget`, the target's path is used instead. |
| `TimeoutSeconds` | no | `60` | Wall-clock limit for the whole run; then the process tree is killed (`timedOut`). |
| `Environment` | no | `{}` | Extra environment variables for the tool, on top of the worker's own. Values of 6 or more characters are masked as `***` everywhere the result leaves the machine. |
| `ExampleArgs` | no | | `{ "param": "value" }` strings, converted to each param's type, shown in the catalog example. Give one for every required param. |

Each `Params` entry:

| Field | Default | Meaning |
|---|---|---|
| `Type` | `string` | `string`, `integer`, `number` or `boolean`. `object` and `array` are refused: an argument is text. |
| `Required` | `false` | Whether the request must send it. A required param must appear in `Args`. A boolean cannot be required. |
| `Description` | | Shown to the cloud. Write it for a model: what the value means and what is accepted. |
| `Pattern` | strings: `^(?!-)[^\r\n]{0,500}\z` | Regex the **whole** string value must match. Ignored for non-strings. |
| `Example` | | One example value (text, converted to the param's type). It must satisfy the pattern. |

Param names: lowercase first letter, then letters, digits or `_` (`project`, `sinceDate`). `targetPath` and
`outputFile` are reserved.

## Placeholders and how arguments are built

Argument items are templates. A `{name}` in an item is replaced by a value:

| Placeholder | Value | Allowed when |
|---|---|---|
| `{param}` | The request's value for that param. | A declared `string`, `integer` or `number` param. |
| `{targetPath}` | The local path of the requested target. | `RequiresTarget` is `true`. |
| `{outputFile}` | A file in the job's artifact folder (`result.md` or `result.csv`) that the tool must write. | `Output` is `markdown` or `csv`. |

Rules:

- **One item = one argument, always.** A placeholder can be the whole item (`{project}`) or part of it
  (`--project={project}`, `prefix-{project}`), but a value is never split at spaces or quotes and never re-parsed by a
  shell. No `cmd.exe` is involved. `A & B | C` arrives as exactly that one string.
- Numbers are written with the invariant culture (`1.5`, never `1,5`).
- `{` and `}` always start/end a placeholder. A literal brace cannot be written; an unbalanced brace or an unknown
  name is a startup error.
- Items of `Args` come first, in order. Then the `OptionalArgs` entries of the params that were sent, ordered by param
  name (alphabetically), each entry's items in order.
- An `Args` item that uses an **optional** param is left out when that param is not sent (`--status={status}` simply
  disappears). To keep an option and its value together, use `OptionalArgs`: `"status": ["--status", "{status}"]`.
- A **boolean** param is a switch: declare it, then `"verbose": ["--verbose"]` in `OptionalArgs`. The items are added
  when the value is `true`, left out when `false` or absent. A boolean has no value, so it cannot be a placeholder.
- A required param must be used in `Args`. An optional param must be used in `Args` or have an `OptionalArgs` entry; an
  `OptionalArgs` entry for a value param must use its own `{param}`. Inside an `OptionalArgs` entry you may use that
  param, required params, `{targetPath}` and `{outputFile}`, but not another optional param.
- **Option injection.** A value that starts with `-` could be read by the tool as an option. The default string pattern
  forbids it. If you write your own `Pattern` and an argument starts with the value (`{project}`, `{project}.md`),
  startup refuses a pattern that accepts `-x`; begin the pattern with `(?!-)`, or put the value after an option
  (`--project={project}`).
- A value containing a NUL character is refused at run time.

Example: `Args` = `["export", "--project={project}", "--out", "{outputFile}"]`, `OptionalArgs` =
`{ "status": ["--status", "{status}"], "verbose": ["--verbose"] }`, request
`{"project":"DEMO","status":"In Progress","verbose":true}` runs

```text
<exe> export --project=DEMO --out <job>/artifacts/result.md --status "In Progress" --verbose
```

(shown with quotes for readability; the program receives `In Progress` as one argument).

## What the cloud receives

| `Output` | On success | If the tool wrote nothing / wrong output |
|---|---|---|
| `markdown` / `csv` with `{outputFile}` | The file the tool wrote becomes `artifacts/<requestId>/result.md` (`.csv`). Summary: `Wrote result.md (12.3 KB).` | File missing: `execution_failed`, "did not write its output file". |
| `markdown` / `csv` without `{outputFile}` | The tool's **stdout** becomes the file. | Empty stdout gives an empty file. |
| `json` | `result` is the parsed JSON. Over 256 KB it is published as `result.json` and `result` is empty. | Not valid JSON: `execution_failed`. |
| `text` | `result` = `{ "exitCode": 0, "output": "...", "truncated": false }` (last 32,768 characters of stdout). | |

- **Non-zero exit code**: status `failed`, code `execution_failed`, message `'name' exited with code N: <last 2000
  characters of stderr>` (stdout if stderr is empty). No artifacts are published for a failed run.
- **Timeout** or worker shutdown: the whole process tree is killed; status `timedOut` / `interrupted`.
- **Masking**: every `Environment` value of 6+ characters is replaced by `***` in `result`, the summary, error messages
  and in the published files (including files the tool wrote itself). The local `stdout.log`/`stderr.log` are not masked.
  Secrets that come only from the user's own environment variables (not listed in `Environment`) are **not** masked, so
  list a secret in `Environment` if the tool might print it.
- **Limits**: all artifacts of one job together may not exceed `MaxArtifactMegabytes` (default 20); otherwise the job
  fails with `artifact_too_large` and nothing is published.
- **The tool's environment**: the worker's own environment plus `Environment`. stdin is closed immediately. No console
  window is opened. Jobs run one at a time.

---

## Worked example (hypothetical)

> **This is an example, not a real tool.** The program `backlog-export.js`, its options and the paths are invented
> to show the shape of a complete setup. Do not copy values; use what step 1 found out about the real tool.

### Step 1: what the user's tool looks like

```text
> node C:\Tools\backlog\backlog-export.js --help
Usage: backlog-export.js export --project <key> [--status <status>] --out <file> [--include-closed]
  --project         project key, e.g. DEMO                      (required)
  --status          Open | InProgress | Done                    (optional)
  --out             file to write the Markdown report to        (required)
  --include-closed  also list closed items
Environment: BACKLOG_API_TOKEN must hold an API token.
Exit code 0 on success, 1 on error (message on stderr).
Subcommands also available: export, create, close, delete.
```

### Step 2: what is safe

`export` only reads, so it is exposed. `create`, `close` and `delete` change the backlog: **not exposed** (they are not
reachable, because `export` is fixed in `Args`). The cloud may choose the project, a status and whether to include
closed items; all are plain values that cannot change what the tool does.

### Step 3 and 4: the proposed entry

```json
{
  "AgentBridge": {
    "Capabilities": {
      "backlog.export": {
        "Type": "cli",
        "Description": "Exports the backlog items of a project as a Markdown report, grouped by epic, with status and priority.",
        "UseWhen": "When you need the current scope of a project before analysing or writing requirements.",
        "Exe": "node",
        "Args": [
          "C:/Tools/backlog/backlog-export.js",
          "export",
          "--project={project}",
          "--out",
          "{outputFile}"
        ],
        "OptionalArgs": {
          "status": [ "--status", "{status}" ],
          "includeClosed": [ "--include-closed" ]
        },
        "Output": "markdown",
        "TimeoutSeconds": 120,
        "Environment": {
          "BACKLOG_API_TOKEN": "<the user's token: only in appsettings.Local.json>"
        },
        "Params": {
          "project": {
            "Type": "string",
            "Required": true,
            "Description": "Project key, 2 to 10 capital letters or digits, e.g. DEMO.",
            "Pattern": "[A-Z][A-Z0-9]{1,9}",
            "Example": "DEMO"
          },
          "status": {
            "Type": "string",
            "Description": "Only items with this status: Open, InProgress or Done. Omit for all.",
            "Pattern": "Open|InProgress|Done",
            "Example": "Open"
          },
          "includeClosed": {
            "Type": "boolean",
            "Description": "Also list closed items. Default false."
          }
        },
        "ExampleArgs": { "project": "DEMO", "status": "Open" }
      }
    }
  }
}
```

Notes on the choices: `node` plus the script path is the program (not a `.cmd` wrapper); `export` is a fixed literal
argument, so nothing else of the tool is reachable; `--project={project}` keeps a hostile value from being read as an
option; the report is written to `{outputFile}` so it is published as `result.md`; the token is in `Environment`, so it
is masked even if the tool echoes it.

### Step 6: running it

```powershell
dotnet run --project src/AgentBridge -- validate
dotnet run --project src/AgentBridge -- run backlog.export --args '{\"project\":\"DEMO\",\"status\":\"Open\"}'
```

The response (shortened) and the file on stderr:

```json
{ "status": "succeeded", "capability": "backlog.export", "summary": "Wrote result.md (4.2 KB).", "artifacts": [] }
```
```text
Artifact (local only, not published): <LocalDataRoot>\local-runs\local-...\artifacts\result.md
```

(`artifacts` is empty for a local run because nothing is published; a real request lists
`artifacts/<requestId>/result.md`.) Through Box, the cloud sends

```json
{ "schemaVersion": 1, "requestId": "req-2026-001", "capability": "backlog.export",
  "args": { "project": "DEMO", "status": "Open", "includeClosed": false } }
```

and receives `responses/req-2026-001.json` with `"status": "succeeded"` and
`"artifacts": [{ "path": "artifacts/req-2026-001/result.md", ... }]`.

### Variants

A **PowerShell script** (call PowerShell itself, never the `.ps1` or a `.cmd`):

```json
"Exe": "powershell",
"Args": [ "-NoProfile", "-File", "C:/Tools/export.ps1", "-Project", "{project}" ]
```

A tool that works on a **local folder** (a repository checkout), result as JSON:

```json
"Exe": "C:/Tools/codemap/codemap.exe",
"RequiresTarget": true,
"Args": [ "summary", "--root", "{targetPath}", "--format", "json" ],
"Output": "json"
```

with `"Targets": { "BillingSystem": { "Path": "D:/Repos/billing", "Description": "Billing system source code." } }`.

---

## Troubleshooting

`validate` and the worker print the config key first. `<n>` is the entry's capability name.

| Message or symptom | Cause | Fix |
|---|---|---|
| `...:Exe '<name>' was not found on PATH` | The program is not installed or not on `PATH` for the user running the worker. | Ask the user where it is; use its absolute path in `Exe`. Restart the worker after changing `PATH` (it is read at startup). |
| `...:Exe '<path>' does not exist` | Wrong path, or a folder, or a typo. | Check with `Test-Path`; use forward slashes. |
| `...:Exe '...' is a relative path` | `Exe` has folders but is not absolute. | Use an absolute path, or a bare name on `PATH`. |
| `...:Exe '...' is a Windows batch file` | `.bat`/`.cmd` (also `npm`, `npx`, `yarn` and many shims resolve to `.cmd`). Windows runs those through `cmd.exe`, which re-reads arguments (`& | ^ %`), so they are refused. | Call the real program: `node` with `["script.js"]`, `python` with `["script.py"]`, `powershell` with `["-NoProfile","-File","script.ps1"]`. For an npm package, run its JS entry file with `node`. |
| `<n> could not be started on this machine (error N)` | The file exists but is not a program Windows can start (a `.ps1`, `.js`, a document) or has no permission. | Use the interpreter as `Exe` and the file as the first argument. |
| `...:Args[i] ... {x} is not a declared param` | Placeholder without a matching `Params` entry (or a typo). | Declare the param or fix the name. Names are case-sensitive. |
| `...:Args[i] ... without a matching '{' / '}'` | A literal brace in an argument. | Literal braces are not supported. If the tool needs one, wrap the call in a small script file and call that. |
| `...:Params:x is required but no Args item uses {x}` | A required param is not wired in. | Add `{x}` to `Args`, or make it optional and add an `OptionalArgs` entry. |
| `...:Params:x is declared but used nowhere` | Optional param with no `Args` item and no `OptionalArgs` entry. | Wire it or delete it. |
| `...:OptionalArgs:x never uses {x}` | A value param's entry does not contain its value. | `"x": ["--x", "{x}"]`. For an on/off flag use a `boolean` param. |
| `...{x} is a boolean` | A boolean was used as a placeholder or has no `OptionalArgs` entry. | `"x": ["--flag"]` in `OptionalArgs`; no placeholder. |
| `...:Params:x:Pattern accepts values that start with '-'` | Custom pattern, and an argument starts with the value. | Start the pattern with `(?!-)` or write `--x={x}`. |
| `...:Params:x:Type 'object' is not supported` | An argument is text. | Use `string` with a tight pattern (or several params). |
| `...ExampleArgs:x '...' does not match the param's Pattern` | The example would be rejected by the pattern. | Fix the example or the pattern. |
| `...:WorkingDirectory '...' does not exist` | Folder missing on this machine. | Create it or fix the path. |
| `...:Output 'xml' is not supported` | | `markdown`, `csv`, `json` or `text`. |
| Nothing from this entry shows up in `validate` | `Enabled` is `false`, the JSON is in the wrong file, or the file has a syntax error. | Check braces/commas and that the entry is under `AgentBridge` → `Capabilities`. The worker reads the copy of `appsettings.Local.json` in the build output, and `dotnet run` refreshes that copy while it builds: do not use `--no-build` after editing the file. |
| `rejected`, `invalid_args`: `args.x must match the pattern ...` | The request's value breaks the pattern (a leading `-`, a line break, too long, wrong form). | Working as designed. If the legitimate values are rejected, widen the pattern deliberately; never drop it. |
| `rejected`, `invalid_args`: `args.x is not an accepted arg` | The cloud sent an arg that is not declared. | Declare it only if it is safe; otherwise correct the request. |
| `timedOut`, code `timeout` | The run took longer than `TimeoutSeconds`, or the tool is waiting (a prompt, a login, a lock). | Time the tool by hand; raise `TimeoutSeconds`; make sure it never waits for input (stdin is closed). Read `stdout.log`/`stderr.log`. |
| `failed`: `'<n>' exited with code N: ...` | The tool failed. | The message has the end of stderr; the full text is in `stderr.log` in the job folder. Typical: wrong credentials, a project that does not exist, a missing environment variable (add it to `Environment`). |
| `failed`: `...did not write its output file` | `{outputFile}` was passed but the tool exited 0 without writing it (wrong option name, it prints to stdout instead). | Check the tool's help; for stdout output remove `{outputFile}` from `Args` and keep `Output` markdown/csv. |
| `failed`: `...output is not valid JSON` | `Output: json` but stdout has extra text (banners, warnings, progress). | Use the tool's quiet/JSON-only option, or `Output: text`/`markdown`. |
| Vietnamese looks garbled (`Ä`, `Ã`, `?`) | The tool writes in the console's legacy code page. | Make the tool write UTF-8 (`[Console]::OutputEncoding` for PowerShell, `PYTHONIOENCODING=utf-8` in `Environment` for Python, a UTF-8 option for others). |
| `failed`, `artifact_too_large` | Results exceed `MaxArtifactMegabytes`. | Narrow the result (a filter param), or ask the user to raise the limit. |
| A secret appears in a result | It is not listed in the entry's `Environment` (only those are masked) or is shorter than 6 characters. | Move it into `Environment`; better, stop the tool from printing it. |
| The cloud does not see the capability | The worker was not restarted, or the cloud holds an old catalog. | Restart the worker; check `catalog.json` in Box. Responses carry `catalogVersion`; a mismatch tells the cloud to re-read. |

When something fails and the cause is not obvious: run the exact command by hand from the working directory the
bridge uses (the target folder, `WorkingDirectory`, or the job folder), with the same environment variables, and
compare with `stdout.log` / `stderr.log`.

## Checklist before you say it is done

- [ ] The user approved this exact entry (step 4).
- [ ] Read-only, no side effects reachable (`Args` fixes the operation; no free-form flags or paths).
- [ ] Name is final and business-oriented; `Description` and `UseWhen` mention no paths, executables or credentials.
- [ ] Every param has a `Description` and a tight `Pattern` (or the safe default); examples satisfy the patterns.
- [ ] Secrets are only in `appsettings.Local.json` `Environment` or user environment variables, not in git.
- [ ] `validate` prints `Configuration is valid.`; `run` succeeded with real values and the result was reviewed;
      a bad request was rejected; a tool failure produced a readable error.
- [ ] The worker was restarted and the capability is in `catalog.json`.
- [ ] You told the user what was exposed and where the machine-specific file is (`src/AgentBridge/appsettings.Local.json`,
      not in git, so it must be recreated on another machine).
