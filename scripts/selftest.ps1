<#
.SYNOPSIS
  End-to-end self-test of AgentBridge on this machine, isolated from the real configuration and Box folder.

.DESCRIPTION
  Builds the solution, copies the worker to .local\selftest\app and runs it against a simulated Box folder in
  .local\selftest\box. Stand-in tools from tests\AgentBridge.TestTools play a backlog exporter (CLI) and a
  code-map server (MCP). Your appsettings.Local.json, your real Box folder and a worker that is already running
  are not touched. Prints PASS/FAIL per check; exit code 0 when everything passed.

.PARAMETER FxVersion
  Run on a specific installed .NET runtime, e.g. 10.0.12 (see dotnet --list-runtimes). Default: the normal launcher,
  which uses .NET 8 when installed and .NET 10 otherwise.

.PARAMETER NoBuild
  Skip dotnet build (use the existing Debug build).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\selftest.ps1
#>
param([string]$FxVersion = "", [switch]$NoBuild)

$ErrorActionPreference = "Continue"
if ($PSVersionTable.PSVersion.Major -ge 7) { $PSNativeCommandArgumentPassing = "Legacy" }

$repo = Split-Path -Parent $PSScriptRoot
$root = Join-Path $repo ".local\selftest"
$app = Join-Path $root "app"
$box = Join-Path $root "box\AgentBridge"
$inbox = Join-Path $box "inbox"
$data = Join-Path $root "data"
$demoRepo = Join-Path $root "demo-repo"
$logs = Join-Path $root "logs"
$toolsDll = Join-Path $repo "tests\AgentBridge.TestTools\bin\Debug\net8.0\AgentBridge.TestTools.dll"
$utf8 = New-Object System.Text.UTF8Encoding $false
$script:failed = 0
$script:procs = @()

# Only this script's settings: no Development settings, no AgentBridge__* overrides from the user's environment.
$env:DOTNET_ENVIRONMENT = "Production"
Get-ChildItem env: | Where-Object { $_.Name -like "AgentBridge__*" } | ForEach-Object { Remove-Item "env:$($_.Name)" }

function Check([string]$Label, [bool]$Condition) {
    if ($Condition) { Write-Output "PASS  $Label" } else { Write-Output "FAIL  $Label"; $script:failed++ }
}

function Wait-Until([scriptblock]$Condition, [int]$Seconds = 30) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

# Written as .tmp (ignored by the worker) and renamed, the way a sync client finishes a file.
function Drop([string]$Name, [string]$Content, [Nullable[datetime]]$LastWrite = $null) {
    $tmp = Join-Path $inbox "$Name.tmp"
    [IO.File]::WriteAllText($tmp, $Content, $utf8)
    if ($LastWrite) { (Get-Item $tmp).LastWriteTime = $LastWrite }
    Rename-Item -Path $tmp -NewName $Name
}

function Ping-Json([string]$Id, [string]$Extra = "") {
    '{{"schemaVersion":1,"requestId":"{0}","capability":"agent.ping"{1}}}' -f $Id, $Extra
}

function Response-Exists([string]$Id) { Test-Path (Join-Path $box "responses\$Id.json") }
function Read-Response([string]$Id) { Get-Content -Raw -Encoding UTF8 (Join-Path $box "responses\$Id.json") | ConvertFrom-Json }
function Wait-Response([string]$Id) {
    if (Wait-Until { Response-Exists $Id }) { return Read-Response $Id }
    return $null
}

function Invoke-Bridge([string[]]$BridgeArgs) { & $exe ($prefix + $BridgeArgs) 2>$null }

function Start-Worker([string]$Tag) {
    $startArgs = @{
        FilePath               = $exe
        ArgumentList           = ($prefix + "--AgentBridge:PollSeconds=1")
        WorkingDirectory       = $root
        RedirectStandardOutput = (Join-Path $logs "$Tag.out.log")
        RedirectStandardError  = (Join-Path $logs "$Tag.err.log")
        PassThru               = $true
        NoNewWindow            = $true
    }
    $p = Start-Process @startArgs
    $null = $p.Handle
    $script:procs += $p
    return $p
}

function Stop-Worker($Process) {
    Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    $null = $Process.WaitForExit(10000)
    # Stand-in MCP servers the worker started outlive a forced kill.
    Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
        Where-Object { $_.CommandLine -like "*$toolsDll*mcp-server*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

try {
    Write-Output "=== AgentBridge self-test ==="

    if (-not $NoBuild) {
        dotnet build (Join-Path $repo "AgentBridge.sln") -nologo -v q | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Output "FAIL  dotnet build failed; run it yourself to see the errors."
            exit 1
        }
    }
    if (-not (Test-Path $toolsDll)) {
        Write-Output "FAIL  $toolsDll is missing; build the solution first."
        exit 1
    }

    if (Test-Path $root) { Remove-Item -Recurse -Force $root }
    New-Item -ItemType Directory -Force $app, $logs, $demoRepo | Out-Null
    Copy-Item -Recurse -Path (Join-Path $repo "src\AgentBridge\bin\Debug\net8.0\*") -Destination $app
    Remove-Item -Path (Join-Path $app "appsettings.Local.json"), (Join-Path $app "appsettings.Development.json") -ErrorAction SilentlyContinue

    $dotnet = (Get-Command dotnet).Source
    $config = @{
        AgentBridge = @{
            AgentId       = "selftest"
            BoxRoot       = $box
            LocalDataRoot = $data
            Targets       = @{ DemoRepo = @{ Path = $demoRepo; Description = "Demo repository for the self-test" } }
            McpServers    = @{ codemap = @{ Command = $dotnet; Args = @($toolsDll, "mcp-server"); StartupTimeoutSeconds = 30 } }
            Capabilities  = @{
                "backlog.export" = @{
                    Type           = "cli"
                    Exe            = $dotnet
                    Args           = @($toolsDll, "cli", "backlog", "--project", "{project}", "--out", "{outputFile}")
                    OptionalArgs   = @{ status = @("--status", "{status}") }
                    Params         = @{
                        project = @{ Required = $true; Description = "Project key, e.g. DEMO"; Pattern = "^[A-Z][A-Z0-9]{1,9}\z" }
                        status  = @{ Description = "Only items with this status"; Pattern = "^(open|done)\z" }
                    }
                    Output         = "markdown"
                    Description    = "Exports the backlog items of a project as Markdown."
                    UseWhen        = "When you need the current backlog of a project for analysis."
                    ExampleArgs    = @{ project = "DEMO" }
                    TimeoutSeconds = 60
                }
                "code.search" = @{
                    Type           = "mcp"
                    Server         = "codemap"
                    Tool           = "search_code"
                    RequiresTarget = $true
                    FixedArgs      = @{ root = "{targetPath}" }
                    Description    = "Finds where a business term appears in the source code of a target."
                    UseWhen        = "When you need to know where a business rule or concept is implemented."
                }
            }
        }
    }
    [IO.File]::WriteAllText((Join-Path $app "appsettings.Local.json"), ($config | ConvertTo-Json -Depth 10), $utf8)

    if ($FxVersion) {
        $exe = "dotnet"
        $prefix = @("exec", "--fx-version", $FxVersion, (Join-Path $app "AgentBridge.dll"))
        Write-Output "Runtime: .NET $FxVersion"
    } else {
        $exe = Join-Path $app "AgentBridge.exe"
        $prefix = @()
        Write-Output "Runtime: default launcher"
    }

    # ---- Local commands ----
    $validate = (Invoke-Bridge @("validate")) -join "`n"
    Check "validate: configuration is valid" ($LASTEXITCODE -eq 0 -and $validate -match "Configuration is valid")
    Check "validate: lists the CLI and MCP capabilities and the MCP server" ($validate -match "backlog\.export" -and $validate -match "code\.search" -and $validate -match "MCP servers:\s+codemap")
    $tools = (Invoke-Bridge @("mcp-tools", "codemap")) -join "`n"
    Check "mcp-tools: lists the server's tools" ($LASTEXITCODE -eq 0 -and $tools -match "search_code")
    $run = (Invoke-Bridge @("run", "agent.ping", "--args", '{\"echo\":\"hi\"}')) -join "`n"
    $json = $null; try { $json = $run | ConvertFrom-Json } catch { }
    Check "run agent.ping: clean JSON on stdout, exit 0" ($LASTEXITCODE -eq 0 -and $json.status -eq "succeeded" -and $json.result.echo -eq "hi")
    $run = (Invoke-Bridge @("run", "backlog.export", "--args", '{\"project\":\"DEMO\"}')) -join "`n"
    $json = $null; try { $json = $run | ConvertFrom-Json } catch { }
    Check "run backlog.export: succeeds locally" ($LASTEXITCODE -eq 0 -and $json.status -eq "succeeded")
    $null = Invoke-Bridge @("run", "agent.nope")
    Check "run with an unknown capability exits 1" ($LASTEXITCODE -eq 1)
    $null = Invoke-Bridge @("run")
    Check "run without a capability exits 2" ($LASTEXITCODE -eq 2)

    # ---- Worker: startup ----
    $workerA = Start-Worker "worker-a"
    Check "startup publishes catalog.json and README.md" (Wait-Until { (Test-Path (Join-Path $box "catalog.json")) -and (Test-Path (Join-Path $box "README.md")) })
    $catalogText = Get-Content -Raw -Encoding UTF8 (Join-Path $box "catalog.json")
    $catalog = $catalogText | ConvertFrom-Json
    Check "catalog lists agent.ping, backlog.export, code.search" (($catalog.capabilities.name -join ",") -eq "agent.ping,backlog.export,code.search")
    $search = $catalog.capabilities | Where-Object { $_.name -eq "code.search" }
    Check "catalog hides the fixed MCP argument" (-not ($search.args.PSObject.Properties.Name -contains "root"))
    Check "catalog leaks no local paths" (-not $catalogText.Contains($repo) -and -not $catalogText.Contains($repo.Replace('\', '\\')))

    $workerB = Start-Worker "worker-b"
    $null = $workerB.WaitForExit(20000)
    Check "a second worker refuses to start (lock held)" ($workerB.HasExited -and $workerB.ExitCode -eq 1)

    # ---- Ping, duplicates ----
    $vi = "xin ch" + [char]0x00E0 + "o"
    $pingContent = Ping-Json "st-ping" (',"args":{{"echo":"{0}"}}' -f $vi)
    Drop "st-ping.json" $pingContent
    $r = Wait-Response "st-ping"
    Check "ping through Box succeeds and keeps Vietnamese text" ($r.status -eq "succeeded" -and $r.result.echo -eq $vi)
    Check "response carries the agent id and catalogVersion" ($r.agent.id -eq "selftest" -and $r.catalogVersion -eq $catalog.catalogVersion)
    Check "the request moves to archive/" (Wait-Until { Test-Path (Join-Path $box "archive\st-ping.json") })
    $before = $r.completedAtUtc
    Drop "st-ping-again.json" $pingContent
    Check "an identical duplicate is archived" (Wait-Until { Test-Path (Join-Path $box "archive\st-ping-again.json") })
    Check "... and not run again" ((Read-Response "st-ping").completedAtUtc -eq $before)
    Drop "st-conflict.json" (Ping-Json "st-ping" ',"args":{"echo":"different"}')
    $reason = Join-Path $box "dead-letter\st-conflict.json.reason.txt"
    Check "same requestId with other content goes to dead-letter/ (duplicate_conflict)" ((Wait-Until { Test-Path $reason }) -and ((Get-Content -Raw $reason) -match "duplicate_conflict"))

    # ---- CLI and MCP through Box ----
    Drop "st-backlog.json" '{"schemaVersion":1,"requestId":"st-backlog","capability":"backlog.export","args":{"project":"DEMO","status":"open"}}'
    $r = Wait-Response "st-backlog"
    $artifact = $r.artifacts | Select-Object -First 1
    Check "CLI capability succeeds and lists its artifact" ($r.status -eq "succeeded" -and $artifact.path -eq "artifacts/st-backlog/result.md")
    $md = Join-Path $box "artifacts\st-backlog\result.md"
    $mdText = if (Test-Path $md) { Get-Content -Raw -Encoding UTF8 $md } else { "" }
    Check "the artifact is in Box with the tool's Markdown (UTF-8)" ($mdText -match "DEMO" -and $mdText -match ("B" + [char]0x00E1 + "o c" + [char]0x00E1 + "o"))

    Drop "st-search.json" '{"schemaVersion":1,"requestId":"st-search","capability":"code.search","target":"DemoRepo","args":{"query":"settlement","max_results":5}}'
    $r = Wait-Response "st-search"
    $text = ($r.result | ConvertTo-Json -Depth 10)
    Check "MCP capability succeeds with the fixed root from the target" ($r.status -eq "succeeded" -and $text -match "query=settlement" -and $text -match "demo-repo")
    Drop "st-search-2.json" '{"schemaVersion":1,"requestId":"st-search-2","capability":"code.search","target":"DemoRepo","args":{"query":"order"}}'
    Check "a second MCP call works on the running server" ((Wait-Response "st-search-2").status -eq "succeeded")

    # ---- Rejections ----
    $cases = @(
        @{ Id = "st-unknown"; Json = '{"schemaVersion":1,"requestId":"st-unknown","capability":"agent.nope"}'; Code = "unknown_capability" },
        @{ Id = "st-schema"; Json = '{"schemaVersion":2,"requestId":"st-schema","capability":"agent.ping"}'; Code = "unsupported_schema_version" },
        @{ Id = "st-expired"; Json = (Ping-Json "st-expired" ',"expiresAtUtc":"2020-01-01T00:00:00Z"'); Code = "expired" },
        @{ Id = "st-badtype"; Json = (Ping-Json "st-badtype" ',"args":{"echo":123}'); Code = "invalid_args" },
        @{ Id = "st-target"; Json = (Ping-Json "st-target" ',"target":"C:\\Windows"'); Code = "invalid_request" },
        @{ Id = "st-injection"; Json = '{"schemaVersion":1,"requestId":"st-injection","capability":"backlog.export","args":{"project":"DEMO & del C:\\"}}'; Code = "invalid_args" },
        @{ Id = "st-option"; Json = '{"schemaVersion":1,"requestId":"st-option","capability":"backlog.export","args":{"project":"--help"}}'; Code = "invalid_args" },
        @{ Id = "st-extra"; Json = '{"schemaVersion":1,"requestId":"st-extra","capability":"backlog.export","args":{"project":"DEMO","exe":"cmd.exe"}}'; Code = "invalid_args" },
        @{ Id = "st-root"; Json = '{"schemaVersion":1,"requestId":"st-root","capability":"code.search","target":"DemoRepo","args":{"query":"x","root":"C:\\"}}'; Code = "invalid_args" },
        @{ Id = "st-notarget"; Json = '{"schemaVersion":1,"requestId":"st-notarget","capability":"code.search","args":{"query":"x"}}'; Code = "target_required" }
    )
    foreach ($case in $cases) { Drop "$($case.Id).json" $case.Json }
    foreach ($case in $cases) {
        $resp = Wait-Response $case.Id
        Check "$($case.Id) is rejected with $($case.Code)" ($resp.status -eq "rejected" -and $resp.error.code -eq $case.Code)
    }

    # ---- Files that are not usable requests ----
    Drop "st-broken.json" "{ not json" (Get-Date).AddMinutes(-5)
    Check "an old unreadable file goes to dead-letter/" (Wait-Until { Test-Path (Join-Path $box "dead-letter\st-broken.json") })
    Drop "st-young.json" '{"schemaVersion":1,"requestId":"st-yo'
    Start-Sleep -Seconds 3
    Check "a partly synced file waits in inbox/" ((Test-Path (Join-Path $inbox "st-young.json")) -and -not (Response-Exists "st-young"))
    [IO.File]::WriteAllText((Join-Path $inbox "st-young.json"), (Ping-Json "st-young"), $utf8)
    Check "... and is processed once complete" (Wait-Until { Response-Exists "st-young" })
    Drop "st-evil.json" '{"schemaVersion":1,"requestId":"../evil","capability":"agent.ping"}'
    Check "an unsafe requestId goes to dead-letter/ and nothing is written outside responses/" ((Wait-Until { Test-Path (Join-Path $box "dead-letter\st-evil.json") }) -and -not (Test-Path (Join-Path $box "evil.json")))

    # ---- Crash and recovery ----
    Stop-Worker $workerA
    $crash = Join-Path $data "jobs\st-crash"
    New-Item -ItemType Directory -Force $crash | Out-Null
    [IO.File]::WriteAllText((Join-Path $crash "request.json"), (Ping-Json "st-crash" ',"correlationId":"st"'), $utf8)
    $workerC = Start-Worker "worker-c"
    $r = Wait-Response "st-crash"
    Check "a job cut off by a crash is reported as interrupted, not re-run" ($r.status -eq "interrupted" -and $r.capability -eq "agent.ping" -and $r.correlationId -eq "st")
    Drop "st-after.json" (Ping-Json "st-after")
    Check "new requests work after recovery" ((Wait-Response "st-after").status -eq "succeeded")
    Stop-Worker $workerC

    $tmps = @(Get-ChildItem -Recurse -File $box | Where-Object { $_.Name -like "*.tmp" })
    Check "no temp files were left inside the Box folder" ($tmps.Count -eq 0)
}
finally {
    foreach ($p in $script:procs) { if (-not $p.HasExited) { Stop-Worker $p } }
}

Write-Output ""
if ($script:failed -eq 0) {
    Write-Output "ALL PASSED. Logs: $logs"
    exit 0
}
Write-Output "$($script:failed) FAILED. Worker logs: $logs"
exit 1
