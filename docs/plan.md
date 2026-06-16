# Dispatch Plan

## 1. Product Goal

Dispatch is a Windows-native C# automation runner packaged as a Windows single-file executable with an optional PowerShell module wrapper.

Dispatch lets an administrator run ad-hoc scripts and commands, apply declared YAML jobs, copy files, inspect hosts, inspect logs, and manage credential references across Windows endpoints through selectable transports such as PsExec, PSRP, or raw WinRM. Dispatch has an Ansible-style operating model for hosts, jobs, transports, logs, and artifacts, but it is not a package manager, endpoint agent, or full configuration-management platform.

## 2. Scope

The detailed CLI design contract lives in `docs/cli-design.md`. This roadmap is the implementation source of truth; when the two differ, update both in the same slice.

### v1 must ship

- `dispatch.exe` for Windows `win-x64`.
- Spectre.Console.Cli command surface with predictable root help, command help, validation errors, and typed command settings.
- Command tree: `apply`, `run ps|cmd|exe`, `push`, `hosts list|test|validate|graph|vars`, `logs list|show|tail|export|retry`, `creds add|list|test|remove`, `doctor`, `init job|hosts|config|all`, and `version`.
- Automation CLI for v1 PowerShell script execution through `dispatch run ps <script.ps1>` and compatibility routing for the existing `dispatch run --script ...` shape until module/install callers migrate.
- Target selection through inventory, direct host selectors, comma-separated selectors, and simple host files.
- Target input from `--computer-name` and `--target-file`.
- Raw WinRM transport.
- PSRP transport.
- Direct PowerShell script execution by default.
- Script transfer/preparation under `C:\ProgramData\Dispatch` when the selected transport requires an endpoint-local script path.
- Throttled multi-target execution.
- Expected exit code handling, default `[0]`, with installer-friendly support for `[0,3010]`.
- Local run/log folder with admin logs, CSV summary, JSON summary, per-target outputs, and an event stream suitable for live rendering and NDJSON output.
- Spectre.Console live progress/status/dashboard rendering driven by a single renderer over execution events; worker threads must not write directly to the console.
- Stable output modes: `rich`, `table`, `json`, `ndjson`, and `yaml`, where JSON/NDJSON are non-decorative automation contracts.
- YAML configuration, inventory, and job file schema surfaces, with script-first execution implemented before richer task vocabulary.
- PowerShell module that wraps the bundled EXE.
- Private Azure Repos source install flow: `git clone`, build, install module, validate, and clean up source.
- Local install script deployment for `CurrentUser` and `AllUsers` module scopes.

### v1 should ship if feasible

- Dry-run planning output for all local inputs.
- `dispatch doctor` / `Test-Dispatch` prerequisite checks.
- Basic artifact copy-back rules.
- Release ZIP packaging as a convenience artifact.

### Out of scope for v1

- Additional PsExec transport expansion beyond the current partial implementation and existing regression coverage.
- PsExec-first live validation in environments where `\\<device>\C$` admin-share staging is unavailable.
- Installer/media payload staging.
- Azure Blob payload download, SAS generation, or SAS management.
- Azure Key Vault secret retrieval or runtime secret handoff.
- Dispatch-managed credential, SAS, Key Vault, or runtime secret handoff for any transport.
- Managed/harness execution mode.
- Retry policy beyond basic failure reporting.
- MSI installer.
- Azure Artifacts publishing.
- Binary PowerShell cmdlets.

### Current transport priority

As of 2026-06-16, the next transport implementation priority is WinRM-based execution: raw WinRM first, then PSRP. The current codebase still implements only the PsExec execution path, but further PsExec-first roadmap work is deferred because the active validation environment does not provide reliable `\\<device>\C$` admin-share staging. Test hosts `82H9704` and `92H9704` are now reserved for WinRM-based live validation rather than PsExec-first validation.

## 3. Non-Goals

- No open-ended Ansible-compatible playbook engine or module ecosystem.
- No permanent endpoint agent.
- No Azure Files identity/auth framework.
- No CredSSP/delegation automation.
- No secret vault system.
- No Linux/macOS target support.
- No full software inventory/configuration-management model beyond declared jobs, host selection, and small task vocabulary.
- No installer/media payload staging in v1.
- No Azure Blob download/SAS orchestration in v1; scripts own their own external payload access.
- No remote launcher/harness requirement in v1.

## 4. Architecture

### Runtime model

- `Dispatch.Core` owns planning, target normalization, script transfer/preparation, orchestration, result models, logging abstractions, artifact collection, and transport interfaces.
- `Dispatch.Transports.PsExec` owns PsExec command construction and captured process execution.
- `Dispatch.Transports.Psrp` is a planned transport using PowerShell SDK remote runspaces and the PowerShell Remoting Protocol.
- `Dispatch.Transports.WinRm` is a planned raw WinRM transport using WS-Management shell/command semantics.
- `Dispatch.Cli` owns `dispatch.exe`, Spectre.Console.Cli command routing, automation commands, operator output, live rendering, and structured output modes.
- `Dispatch.PowerShell` owns wrapper functions such as `Start-Dispatch`, `Invoke-DispatchScript`, `Invoke-DispatchJob`, and `Test-Dispatch`.

### Technology choices

- Language: C#.
- Platform: .NET.
- Initial runtime identifier: `win-x64`.
- Packaging target: single-file, self-contained Windows executable.
- CLI parser/routing: `Spectre.Console.Cli` with typed command settings, predictable help, validation errors, dependency injection, and a compatibility adapter for existing v1 `dispatch run --script` callers until they are migrated.
- Console UX: `Spectre.Console` for root help, command help, tables, status displays, progress displays, live dashboards, final summaries, and rich terminal output.
- Live output model: parse/validate output uses stable text/tables; preflight uses `Status` for indeterminate work and `Progress` for measurable work; execution uses one `LiveDisplay` renderer consuming a run-event stream; final summaries are printed after live rendering ends.
- Console concurrency rule: prompts and confirmations must complete before live rendering starts, and only the renderer writes to `AnsiConsole` while a live display is active.
- Application host: `Microsoft.Extensions.Hosting`.
- Configuration: `Microsoft.Extensions.Configuration.Json`.
- Logging: `Microsoft.Extensions.Logging.Console` in the CLI and `Microsoft.Extensions.Logging.Abstractions` in core libraries.
- Serialization: `System.Text.Json`.
- Tests: `xUnit`, with `FluentAssertions` optional.

### Repo layout target

```text
Dispatch/
  src/
    Dispatch.Core/
    Dispatch.Transports.PsExec/
    Dispatch.Transports.Psrp/
    Dispatch.Transports.WinRm/
    Dispatch.Cli/
  module/
    Dispatch/
      Dispatch.psd1
      Dispatch.psm1
      schemas/
      examples/
  packaging/
    bootstrap-install.ps1
    install.ps1
    install-from-source.ps1
  tests/
    Dispatch.Core.Tests/
    Dispatch.Cli.Tests/
  docs/
    plan.md
  workflow/
    build/
      implementation-plan.md
  azure-pipelines.yml
```

## 5. Job Model

Every execution is a job.

```text
DispatchJob
  RunId
  Targets
  Payload
  Transport
  ExecutionContext
  ScriptTransferPolicy
  TimeoutPolicy
  RetryPolicy
  ExpectedExitCodes
  ArtifactPolicy
  ResultPolicy
```

`Payload` is one of:

```text
ScriptPayload
  ScriptPath
  ScriptArguments

CommandPayload
  CommandLine
  Shell
  WorkingDirectory
```

v1 focuses on PowerShell script execution. Command execution is part of the shared model so PSRP and raw WinRM can support Ansible-style command execution without adding a separate execution path.

### Request support matrix

Dispatch has one request model, but each transport/payload combination must be explicitly enabled.

```text
psexec + ScriptPayload   = implemented; deferred behind WinRM-based roadmap work
psexec + CommandPayload  = modeled; deferred unless explicitly enabled later
psrp   + ScriptPayload   = next transport roadmap work after raw WinRM
psrp   + CommandPayload  = same transport family; not yet implemented
winrm  + ScriptPayload   = next transport roadmap work
winrm  + CommandPayload  = same transport family; not yet implemented
```

`DispatchRequest` may represent script and command payloads from the beginning, but v1 validation must reject unsupported transport/payload combinations before probing or touching endpoints. This keeps v1 focused on script orchestration while preserving the future Ansible-style command execution surface.

Each endpoint has its own target state:

```text
Pending
Resolving
Probing
PreparingScript
Executing
CollectingArtifacts
Succeeded
Failed
TimedOut
Cancelled
```

### Result schema

Result JSON is the durable file contract for the CLI, PowerShell module, automation, and later package feeds. CSV is a flattened operator-friendly subset. Console output is an operator UI contract, not a raw JSON contract; automation should read explicit result files rather than parse terminal rendering.

Minimum per-run fields:

```text
runId
startedAt
endedAt
durationMs
requestedBy
transport
payloadType
payloadName
targetCount
successCount
failedCount
cancelledCount
timedOutCount
resultPath
```

Minimum per-target fields:

```text
runId
target
transport
payloadType
payloadName
state
exitCode
expectedExitCodes
startedAt
endedAt
durationMs
failureCategory
failureMessage
stdoutPath
stderrPath
resultPath
artifacts
secretHandoffStatus
cleanupStatus
transportMetadata
```

### Failure categories

Failure categories must be stable enum values. Transport-specific details belong in `transportMetadata` and should not replace these common categories.

```text
TargetResolutionFailed
ProbeFailed
PayloadPreparationFailed
ScriptTransferFailed
SecretHandoffFailed
ExecutionFailed
UnexpectedExitCode
TimedOut
ArtifactCollectionFailed
CleanupFailed
Cancelled
TransportUnavailable
AuthenticationFailed
AuthorizationFailed
InternalError
```

## 6. Execution Model

Default execution flow:

1. Parse request.
2. Resolve targets.
3. Build dry-run-capable execution plan.
4. Create local run folder.
5. Start bounded worker pool.
6. Probe endpoint.
7. Prepare the script on the endpoint when the selected transport requires a file path.
8. Execute script directly.
9. Capture exit code/output.
10. Pull logs/artifacts.
11. Write CSV/JSON/admin summary.

Dispatch should run the prepared script directly in v1. Later transports must preserve the same job, target, result, expected-exit-code, artifact, and logging model even when their protocol-specific execution mechanics differ.

### Transport protocol model

Dispatch exposes transports as explicit protocol choices, similar to Ansible's separate `winrm` and `psrp` connection plugins.

```text
--transport psexec
--transport psrp
--transport winrm
```

Each transport must implement the same core contract:

```text
ProbeAsync
PreparePayloadAsync
ExecuteScriptAsync
ExecuteCommandAsync
CollectArtifactsAsync
CleanupAsync
```

Transport methods must return structured results and must not throw for expected endpoint, authentication, transfer, timeout, or script failures. Exceptions are reserved for internal programming errors and are mapped to `InternalError` at the orchestration boundary.

```text
ProbeAsync(TargetContext) -> ProbeResult
PreparePayloadAsync(TargetContext, ExecutionPlan) -> PreparationResult
ExecuteScriptAsync(TargetContext, PreparedPayload) -> ExecutionResult
ExecuteCommandAsync(TargetContext, CommandPayload) -> ExecutionResult
CollectArtifactsAsync(TargetContext, ArtifactPolicy) -> ArtifactResult
CleanupAsync(TargetContext, CleanupPolicy) -> CleanupResult
```

Every transport result includes:

```text
status
failureCategory
message
startedAt
endedAt
durationMs
transportMetadata
```

`ExecutionResult` additionally includes:

```text
exitCode
stdout
stderr
stdoutPath
stderrPath
streamRecords
```

`streamRecords` is optional and mainly used by PSRP for rich PowerShell streams. PsExec and raw WinRM primarily map process stdout/stderr.

Transport capabilities are declared explicitly:

```text
TransportCapabilities
  SupportsScriptExecution
  SupportsCommandExecution
  RequiresEndpointLocalScriptPath
  SupportsNativeFileCopy
  SupportsStreamedFileTransfer
  SupportsPowerShellStreams
  SupportsCurrentUser
  SupportsExplicitCredential
  SupportsRunAsSystem
  SupportsCredentialDelegation
```

### Endpoint prerequisites

Dispatch does not silently remediate endpoint configuration. `dispatch doctor` may report local prerequisites, and probes may classify endpoint readiness, but enabling services, firewall rules, delegation, listeners, trusted hosts, or endpoint policy remains an operator responsibility.

```text
PsExec:
  - Windows target
  - local administrator rights on target
  - SMB/admin shares reachable
  - psexec.exe available on admin workstation
  - firewall and EDR/AV policy allow PsExec behavior

PSRP:
  - PowerShell remoting enabled on target
  - WinRM listener reachable for PSRP-over-WSMan
  - selected session configuration exists and permits the caller
  - caller has rights required by the endpoint session configuration
  - no automatic WinRM/CredSSP/delegation/trusted-host remediation by Dispatch

Raw WinRM:
  - WinRM listener reachable on target
  - WS-Management shell/command access permitted
  - selected authentication mechanism allowed by endpoint policy
  - Basic authentication only over HTTPS
  - no automatic WinRM/CredSSP/delegation/trusted-host remediation by Dispatch
```

#### PsExec transport

PsExec is the only implemented execution transport today. It uses SMB/admin-share file preparation and PsExec process execution from the admin workstation, but further PsExec-first roadmap work is deferred behind WinRM-based transports because the current validation environment does not provide reliable `\\<device>\C$` admin-share staging.

PsExec example:

```text
psexec \\PC001 -s -h powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\ProgramData\Dispatch\Runs\<RunId>\script\Fix.ps1
```

Command example:

```text
psexec \\PC001 -s -h cmd.exe /c whoami
```

PsExec capabilities:

- Requires SMB/admin-share access for script preparation and artifact copy-back.
- Supports `--run-as-system`.
- Uses the current admin context in v1.
- Does not support Dispatch-managed credential prompting or password handling in v1.
- Should avoid command-line secret passing.

PsExec credential model:

- v1 supports current admin context only.
- v1 does not accept plaintext password arguments.
- v1 does not invoke `psexec -u/-p` because those values are difficult to protect from command-line exposure, logs, dry-run rendering, and process inspection.
- Post-MVP explicit credential support may be added only through a secure prompt, protected in-memory credential object, or approved secret source.
- `--run-as-system` changes the remote process token to local SYSTEM through PsExec; it is not a domain credential delegation feature.

PsExec SAS/secret handoff model:

- v1 has no supported SAS token handoff.
- Operators may pass ordinary non-secret script arguments, but SAS tokens and credentials must not be passed on the PsExec command line.
- Post-MVP PsExec secret handoff must use a protected file model: Dispatch retrieves the secret on the admin workstation, encrypts it for the endpoint or endpoint script, copies the protected file over SMB/admin share, passes only the protected file path to the script, redacts the path/value where needed, and deletes the remote secret file during cleanup.
- PsExec dry-run output must render secret handoff as redacted metadata only; it must never display the SAS token, credential, protected file content, or decrypted value.

#### PSRP transport

PSRP is the planned PowerShell-native remoting transport after raw WinRM. It uses PowerShell SDK remote runspaces through `WSManConnectionInfo` for PSRP-over-WSMan, with an optional future `SSHConnectionInfo` mode for PSRP-over-SSH.

Script example:

```powershell
Invoke-Command -ComputerName PC001 -ScriptBlock {
  & C:\ProgramData\Dispatch\Runs\<RunId>\script\Fix.ps1
}
```

Command example:

```powershell
Invoke-Command -ComputerName PC001 -ScriptBlock {
  whoami
}
```

PSRP capabilities:

- Supports script and command execution through a real PowerShell remoting session.
- Captures PowerShell output, error, warning, verbose, debug, and information streams where practical.
- Supports current user / Negotiate by default.
- Can later support explicit `PSCredential`, certificate authentication, configuration names, and SSH key-based PSRP.
- Does not require SMB/admin shares for the core execution path.
- Should support streamed/chunked script and artifact transfer over the remoting channel.

PSRP credential model:

- Default mode uses current user / Negotiate over WSMan.
- Post-MVP explicit credentials may be accepted as `PSCredential` from the PowerShell wrapper or through a secure CLI prompt; plaintext password command-line flags are not allowed.
- Supported authentication options should be modeled explicitly: `Default`, `Negotiate`, `Kerberos`, certificate authentication, and optional `CredSSP` only when the operator explicitly enables it.
- The transport must support a configurable session configuration name, such as `Microsoft.PowerShell` or `PowerShell.7`.
- PSRP-over-SSH may be added later with key-based authentication through `SSHConnectionInfo`.
- Dispatch must not automatically enable WinRM, CredSSP, delegation, trusted hosts, listeners, firewall rules, or PowerShell remoting policy.

PSRP SAS/secret handoff model:

- Dispatch does not yet have a PSRP transport or supported PSRP SAS token handoff.
- Post-MVP preferred handoff is still the protected secret-file model so scripts receive a file path instead of a raw token.
- For Blob/SAS use cases, Dispatch retrieves the configured secret on the admin workstation or jump box, protects it, transfers the protected secret file to the endpoint over the PSRP remoting channel, and invokes the script with only the protected secret-file path.
- PSRP invocation must pass a secret reference, not the raw secret value, for the default model.
- Example shape:

```powershell
Invoke-Command -ComputerName PC001 -ScriptBlock {
  param($ScriptPath, $SecretFile)
  & $ScriptPath -SecretFile $SecretFile
} -ArgumentList `
  "C:\ProgramData\Dispatch\Runs\<RunId>\script\Install.ps1",
  "C:\ProgramData\Dispatch\Runs\<RunId>\secrets\payload.secret"
```

- The endpoint script is responsible for reading/decrypting the protected secret file and downloading its own payload.
- PSRP may add an advanced in-memory parameter handoff for short-lived secrets over an encrypted remoting channel, but it must be opt-in and fully redacted from dry-run output, logs, result JSON, CSV, terminal rendering, and transport traces.
- Dispatch still does not download installer/media payloads.
- Cleanup must remove any remote secret file/folder and report cleanup failures separately from script execution failures.

#### Raw WinRM transport

Raw WinRM is the next transport implementation priority for environments that want Ansible-style WinRM behavior without depending on PowerShell runspace semantics. It uses WS-Management shell/command operations and maps process stdout, stderr, and exit code into the common Dispatch result model.

Raw WinRM implementation choice:

- Preferred implementation is a .NET wrapper around WinRM Client Shell API / WS-Management shell-command semantics.
- `winrs.exe` may be used as a diagnostic shim or test fixture, but it is not the primary implementation target.
- Raw WinRM must not use `Microsoft.PowerShell.SDK` remote runspaces; that implementation belongs to `Dispatch.Transports.Psrp`.
- The implementation must preserve structured result handling, timeout classification, streamed/chunked payload transfer, and artifact collection without shelling out to a tool that hides protocol state.

Script example:

```text
winrm shell command: powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\ProgramData\Dispatch\Runs\<RunId>\script\Fix.ps1
```

Command example:

```text
winrm shell command: cmd.exe /c whoami
```

Raw WinRM capabilities:

- Supports command execution through WinRM shell/command operations.
- Supports PowerShell script execution by invoking `powershell.exe` as a remote process.
- Captures process stdout, stderr, exit code, and duration.
- Does not capture rich PowerShell streams like PSRP.
- Does not require SMB/admin shares for the core execution path.
- Must implement streamed/chunked script and artifact transfer because raw WinRM is not a normal filesystem copy protocol.
- Must distinguish WS-Man operation timeout from client read/connection timeout.

Raw WinRM credential model:

- Default mode uses current user / Negotiate.
- Post-MVP explicit credentials may be accepted through a secure prompt or protected credential object; plaintext password command-line flags are not allowed.
- Supported authentication options should be modeled explicitly: `Default`, `Negotiate`, `Kerberos`, `NTLM`, `BasicOverHttps`, certificate authentication, and optional `CredSSP` only when the operator explicitly enables it.
- Basic authentication is allowed only over HTTPS.
- Dispatch must not automatically enable WinRM, CredSSP, delegation, trusted hosts, listeners, firewall rules, or WinRM policy.

Raw WinRM SAS/secret handoff model:

- Dispatch does not yet have a raw WinRM transport or supported raw WinRM SAS token handoff.
- Post-MVP preferred handoff is the protected secret-file model transferred over the WinRM channel through streamed/chunked content.
- For Blob/SAS use cases, Dispatch retrieves the configured secret on the admin workstation or jump box, protects it, uploads the protected secret file through chunked WinRM content transfer, and invokes the remote process with only the protected secret-file path.
- Raw SAS tokens must not be rendered into remote command lines, local logs, dry-run output, result JSON, CSV summaries, or terminal output.
- The remote process should receive only a protected secret-file path or an explicitly redacted environment-style input created by Dispatch.
- Example shape:

```text
winrm shell command: powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\ProgramData\Dispatch\Runs\<RunId>\script\Install.ps1 -SecretFile C:\ProgramData\Dispatch\Runs\<RunId>\secrets\payload.secret
```

- The endpoint script is responsible for reading/decrypting the protected secret file and downloading its own payload.
- Because raw WinRM only exposes process stdout/stderr, scripts must avoid echoing secret values; Dispatch must still apply best-effort redaction to captured streams.
- Cleanup must remove any remote secret file/folder and report cleanup failures separately from script execution failures.

Do not require a launcher or harness in v1. The current installer runner intentionally runs `update.ps1` directly, and Dispatch should preserve that simplicity.

## 7. Storage Layout

Local admin machine:

```text
C:\ProgramData\Dispatch\Runs\<RunId>\
  Admin\
    dispatch.log
    results.csv
    results.json
  Targets\
    PC001\
      stdout.txt
      stderr.txt
      result.json
      artifacts\
```

Remote endpoint:

```text
C:\ProgramData\Dispatch\Runs\<RunId>\
  script\
    Fix.ps1
  logs\
  artifacts\
```

## 8. External Payload Boundary

Dispatch does not own installer/media payload retrieval in v1. If a script needs a payload from Azure Blob Storage, Azure Files, SMB, HTTPS, or any other source, the script is responsible for downloading or copying that payload.

This keeps Dispatch focused on script orchestration, execution, logging, and result collection.

Example:

```powershell
Invoke-DispatchScript `
  -ComputerName PC001 `
  -ScriptPath .\Install-App.ps1 `
  -RunAsSystem
```

The script owns payload retrieval:

```powershell
$payloadUri = $env:APPINSTALLER_BLOB_URI
$zipPath = Join-Path $env:TEMP "AppInstaller.zip"

Invoke-WebRequest -Uri $payloadUri -OutFile $zipPath
Expand-Archive -LiteralPath $zipPath -DestinationPath "$env:TEMP\AppInstaller" -Force
& "$env:TEMP\AppInstaller\setup.exe" /quiet
```

Dispatch may pass normal script arguments or environment values, but it does not interpret, generate, persist, or validate SAS tokens in v1. Scripts must avoid writing SAS tokens to their own logs.

Future versions may add a narrow Key Vault secret retrieval and runtime secret handoff feature. That would let Dispatch retrieve a SAS on the admin workstation or jump box and pass it to the script without making Dispatch responsible for Blob payload download or installer/media staging.

## 9. CLI And PowerShell UX

Interactive CLI:

```powershell
dispatch
```

Automation CLI:

```powershell
dispatch run --computer-name PC001,PC002 --script .\Fix.ps1 --transport psexec
dispatch run --computer-name PC001,PC002 --script .\Fix.ps1 --transport psrp
dispatch run --computer-name PC001,PC002 --command "whoami" --transport winrm
```

PowerShell module:

```powershell
Invoke-DispatchScript `
  -ComputerName PC001,PC002 `
  -ScriptPath .\Fix.ps1 `
  -Transport PsExec `
  -ThrottleLimit 10
```

The interactive CLI must be a frontend only. It creates the same request model as automation mode and calls the same `Dispatch.Core` planner/executor.

```text
Interactive wizard
  -> DispatchRequest
  -> Dispatch.Core

Command-line args
  -> DispatchRequest
  -> Dispatch.Core
```

Do not create a separate execution path for interactive mode.

## 10. Packaging And Deployment

Initial release primary distribution:

```powershell
git clone https://dev.azure.com/<org>/<project>/_git/Dispatch
cd Dispatch
.\packaging\bootstrap-install.ps1
Import-Module Dispatch
Start-Dispatch
```

The v1 source installer is designed for a private Azure DevOps repository. It relies on the operator's normal Git authentication, such as Git Credential Manager or an Azure DevOps browser sign-in, rather than passing a PAT on the command line.

`bootstrap-install.ps1` is the primary operator entry point. It must:

1. Validate Windows, PowerShell, Git, .NET SDK, and build prerequisites.
2. Call `install-from-source.ps1` to build and publish `dispatch.exe` as a single-file self-contained `win-x64` executable.
3. Assemble and install the PowerShell module folder with the bundled EXE.
4. Validate the module manifest, bundled EXE, import behavior, and exported commands.
5. Report the installed module path and the installed `dispatch.exe` version.
6. Copy or generate a cleanup helper under `$env:TEMP`.
7. Change the current location outside the cloned source directory.
8. Invoke the cleanup helper to remove the cloned source tree, including the original bootstrap script.
9. Report cleanup success or cleanup failure without uninstalling the already validated module.

Bootstrap cleanup semantics:

- The bootstrap script must not depend on deleting its own currently executing file from inside the source tree.
- Cleanup must run from outside the clone, normally from `$env:TEMP`.
- `-NoCleanup` must be available for developers and troubleshooting.
- Cleanup failure is a warning/final status detail, not a reason to remove the installed module after validation succeeds.

Developer source install flow:

```powershell
git clone https://dev.azure.com/<org>/<project>/_git/Dispatch
cd Dispatch
.\packaging\install-from-source.ps1
Import-Module Dispatch
Start-Dispatch
```

`install-from-source.ps1` is the reusable build/install helper for developers and CI jobs that intentionally keep the source checkout. After a successful bootstrap install, only the installed PowerShell module and bundled executable should remain.

Release convenience packaging:

```text
Pipeline builds dispatch.exe.
Pipeline assembles module folder.
Pipeline creates Dispatch-<version>-win-x64.zip.
User runs install.ps1.
```

PowerShell module strategy starts with a wrapper module that includes the bundled EXE:

```text
Dispatch\
  Dispatch.psd1
  Dispatch.psm1
  bin\
    win-x64\
      dispatch.exe
```

Functions:

```text
Start-Dispatch        # runs dispatch.exe with no args, active CLI
Invoke-DispatchScript # calls dispatch.exe run ...
Invoke-DispatchJob    # calls dispatch.exe run --job ...
Test-Dispatch         # calls dispatch.exe doctor
```

`Start-Dispatch` must pass no arguments so the active CLI starts. Automation functions intentionally pass arguments because the user supplied function parameters.

Later deployment options:

- MSI installer.
- Azure Artifacts PowerShell repository backed by a NuGet feed.
- WinGet/private package feed.
- Binary PowerShell cmdlets that call `Dispatch.Core` directly.

## 11. Roadmap

### MVP Roadmap

#### 1. Foundation And Repository Structure

Objective:
Create the buildable .NET solution, project layout, shared models, and test structure for Dispatch.

Scope:
- Create `Dispatch.Core`, `Dispatch.Transports.PsExec`, `Dispatch.Cli`, and test projects.
- Establish shared job, target, script, transport, execution, and result model types.
- Add dependency injection, logging abstractions, JSON serialization, and basic configuration defaults.
- Add initial build/test commands.

Non-goals:
- No real remote execution.
- No interactive CLI workflow beyond a placeholder command surface.
- No PowerShell module or packaging.

Dependencies:
- None.

Definition of done:
- Solution builds cleanly.
- Unit test project runs.
- Core model types can serialize to JSON.
- CLI can start and print version/help without executing a job.

#### 1.1 Job And Result Model

Objective:
Represent a Dispatch run as a structured job with per-target execution state and serializable results.

Scope:
- Define `DispatchJob`, `DispatchRequest`, `ExecutionPlan`, `TargetExecution`, and result models.
- Define `ScriptPayload` and `CommandPayload`, with v1 validation allowing only supported transport/payload combinations.
- Define target states: `Pending`, `Resolving`, `Probing`, `PreparingScript`, `Executing`, `CollectingArtifacts`, `Succeeded`, `Failed`, `TimedOut`, `Cancelled`.
- Define stable result JSON fields, flattened CSV fields, and common failure category enum values.
- Define expected exit code policy with default `[0]`.
- Add JSON round-trip tests.

Non-goals:
- No endpoint probing.
- No script transfer.
- No transport execution.

Dependencies:
- 1.

Definition of done:
- Job/result models compile and are covered by unit tests.
- JSON output is stable enough for initial CLI and PowerShell wrapper consumption.
- Result objects can represent success, failure, timeout, and cancellation.
- Result objects include the minimum schema fields and common failure categories defined in the Job Model section.

#### 1.2 Application Host And Configuration

Objective:
Create the shared CLI host wiring used by both interactive and automation modes.

Scope:
- Wire `Microsoft.Extensions.Hosting`.
- Register core planner/executor services.
- Register logging and configuration providers.
- Add default values for local run root, remote script root, transport, throttle, and expected exit codes.

Non-goals:
- No user configuration file editor.
- No registry or machine-wide config store.
- No secret storage.

Dependencies:
- 1.1.

Definition of done:
- CLI resolves services through DI.
- Defaults are available through typed options.
- Tests cover default configuration binding where practical.

#### 2. Request Planning And Dry Run

Objective:
Convert CLI/module input into a dry-run-capable execution plan before touching endpoints.

Scope:
- Validate script path, script arguments, output root, remote script root, transport selection, run context, throttle limit, and expected exit codes.
- Validate payload support using the transport/payload support matrix before endpoint probing or remote work.
- Normalize direct execution command shape.
- Generate run ID and planned local/remote paths.
- Add dry-run result output.

Non-goals:
- No endpoint access.
- No PsExec process launch.
- No script copy to endpoints.

Dependencies:
- 1.2.

Definition of done:
- `dispatch run --dry-run` can print or write a complete execution plan.
- Invalid local inputs fail before endpoint work starts.
- Unsupported transport/payload combinations fail before endpoint work starts.
- Tests cover command/path planning and expected exit code policy.

#### 2.1 Target Resolution

Objective:
Resolve target names from command-line input and target files in deterministic order.

Scope:
- Support `--computer-name PC001,PC002`.
- Support `--target-file`.
- Trim whitespace, ignore blank/comment lines, and de-duplicate case-insensitively while preserving first-seen order.
- Preserve source metadata in the plan.

Non-goals:
- No DNS or network probe.
- No inventory grouping.
- No dynamic discovery.

Dependencies:
- 2.

Definition of done:
- Target resolution produces stable ordered target lists.
- Empty or missing target sources fail clearly.
- Tests cover comma-separated input, target files, comments, blanks, and duplicates.

#### 2.2 Local Run Layout

Objective:
Create the local admin-side run folder structure and planned per-target artifact paths.

Scope:
- Create `C:\ProgramData\Dispatch\Runs\<RunId>\Admin`.
- Create planned target roots under `Targets\<DeviceName>`.
- Support overriding output root for tests and non-default operator workflows.
- Ensure file-vs-directory conflicts fail clearly.

Non-goals:
- No endpoint log copy.
- No CSV/JSON final summary writing yet.
- No retention policy.

Dependencies:
- 2.1.

Definition of done:
- Dry-run and real-run planning create/validate local output paths.
- Tests cover default and overridden run roots.
- The plan includes local admin and per-target result paths.

#### 3. Script Transfer And Remote Run Path

Objective:
Prepare the selected script at a target-local Dispatch run path when the selected transport requires endpoint-local execution.

Scope:
- Build a script execution manifest from `--script` and script arguments.
- Copy only the selected script to the endpoint for PsExec v1.
- Store the script under remote `script`.
- Report script transfer failures per target.
- Preserve direct execution semantics; no generated launcher is required.

Non-goals:
- No installer/media payload staging.
- No Azure Blob source handling.
- No endpoint-side download agent.
- No differential sync.
- No compression requirement.

Dependencies:
- 2.2.

Definition of done:
- A target can receive the selected script under `C:\ProgramData\Dispatch\Runs\<RunId>\script`.
- Script transfer failure prevents execution for that target and is reflected in structured results.
- Tests cover script manifest construction and admin-share path conversion.

#### 3.1 Script-Owned External Payload Contract

Objective:
Make the external payload boundary explicit so installer scripts own Blob/SAS payload retrieval.

Scope:
- Document that Dispatch does not stage installer/media payloads.
- Document that scripts must download/copy their own Blob, HTTPS, SMB, or Azure Files payloads.
- Support passing ordinary script arguments through to the script.
- Keep Dispatch logs focused on orchestration metadata and script output, not payload transfer internals.

Non-goals:
- No automatic payload download.
- No automatic script rewriting.
- No SAS generation, persistence, validation, or refresh.
- No guaranteed Blob/share access from the endpoint.
- No Azure Files identity support.

Dependencies:
- 3.

Definition of done:
- The plan documents the script-owned payload boundary.
- CLI/help text does not advertise Dispatch-owned media payload staging.
- Script arguments are represented in planning and execution without changing the script-owned payload model.

#### 4. PsExec Transport And Direct Execution

Objective:
Run the prepared PowerShell script directly on each endpoint through PsExec.

Scope:
- Resolve explicit/default PsExec path.
- Build PsExec argument arrays without string-concatenation quoting bugs.
- Support current context and `--run-as-system`.
- Enforce current admin context only in v1; no plaintext password arguments, no Dispatch-managed `psexec -u/-p`, and no SAS token handoff.
- Execute `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <prepared script>`.
- Capture process exit code, stdout, stderr, duration, and failure reason.
- Classify expected exit codes as success.

Non-goals:
- No WinRM.
- No managed/harness mode.
- No credential prompting or password handling.

Dependencies:
- 3.1.

Definition of done:
- PsExec invocation is test-covered without requiring live endpoints.
- Localhost or shim-based execution can validate success/failure classification.
- Release and validation claims for PsExec execution must include either at least one successful live run against a user-approved reachable Windows endpoint or an explicit blocker showing why the available environment cannot satisfy the required `\\<device>\C$` admin-share staging path.
- Live PsExec verification should use the least invasive script practical, preferably stdout-only behavior or a temporary file under the Dispatch run folder, and must avoid unrelated endpoint changes.
- Nonzero unexpected exit codes fail the target with clear reason.
- Dry-run and command rendering cannot expose credentials, SAS tokens, protected file content, or decrypted values.

#### 4.1 Endpoint Probe And Execution Guardrails

Objective:
Perform minimum endpoint checks before script transfer/execution and report skipped work clearly.

Scope:
- DNS resolution check.
- Connectivity probe.
- Admin share/script path access check for PsExec mode.
- Classify missing transport prerequisites using the endpoint prerequisite matrix.
- Skip script transfer/execution when required probes fail.

Non-goals:
- No full readiness inventory.
- No remediation of WinRM/firewall/admin-share configuration.
- No credential delegation configuration.

Dependencies:
- 4.

Definition of done:
- Probe failures produce structured target failures before PsExec starts.
- Tests cover probe result classification using abstractions/mocks where practical.
- Terminal output distinguishes probe, script transfer, and execution failures.
- Missing prerequisites map to stable failure categories such as `ProbeFailed`, `AuthenticationFailed`, `AuthorizationFailed`, or `TransportUnavailable`.

#### 5. Batch Orchestration And Results

Objective:
Run jobs across multiple targets with bounded concurrency and durable admin-side result output.

Scope:
- Implement throttle-limited target worker pool.
- Maintain per-target state transitions.
- Collect stdout/stderr and endpoint artifacts where available.
- Write `dispatch.log`, `results.csv`, `results.json`, and per-target `result.json`.
- Emit structured progress/result events that terminal renderers and durable log writers can consume.

Non-goals:
- No retry policy.
- No timeout/cancellation beyond basic process completion behavior unless already trivial.
- No terminal rendering ownership in core orchestration.

Dependencies:
- 4.1.

Definition of done:
- Multi-target runs respect throttle limit.
- Result JSON and CSV include run metadata, target metadata, statuses, exit codes, durations, and failure reasons.
- A partial failure job can be distinguished from full success and full failure.

#### 5.1 Artifact Copy-Back

Objective:
Pull declared or default endpoint output artifacts back to the admin run folder.

Scope:
- Copy remote `logs` and `artifacts` folders when present.
- Store copied files under local `Targets\<DeviceName>`.
- Track artifact copy success/failure independently from script execution.

Non-goals:
- No complex glob rule engine in MVP unless simple patterns are already available.
- No log streaming.
- No retention management.

Dependencies:
- 5.

Definition of done:
- Missing artifact folders do not hide the script execution result.
- Copy-back-only failures are represented separately from script failures.
- Tests cover path planning and copy failure classification.

#### 6. CLI Product Surface

Objective:
Replace the legacy command service with the Spectre.Console.Cli command tree from the CLI design while preserving the existing shared planner/executor path for v1 PsExec PowerShell script execution.

Reference:
- `docs/cli-design.md` defines the command tree, global options, output phases, safety rules, and exit-code contract for this roadmap item.

Scope:
- Use `Spectre.Console.Cli` for command registration, settings validation, help, and command dispatch.
- Register the target command tree: `apply`, `run ps|cmd|exe`, `push`, `hosts`, `logs`, `creds`, `doctor`, `init`, and `version`.
- Implement `dispatch run ps <script.ps1>` as the v1 supported ad-hoc execution path and map it to the existing `DispatchRequest`/planner/executor.
- Keep a compatibility adapter for the current `dispatch run --script <path> --computer-name <names>` shape until the PowerShell module and packaging are migrated.
- Implement root help and command help that match the CLI design: short, predictable, no more than three examples per command.
- Implement global option parsing for inventory, target, exclude, transport, credential, concurrency, timeout, config, log directory, run ID, output mode, color/progress flags, quiet, verbose, and trace where supported by the current model.
- Unknown or not-yet-implemented commands must render explicit Spectre.Console errors that identify the roadmap item required; they must not silently pretend support exists.

Current implementation note:
- `apply`, `run ps|cmd|exe`, `push`, `hosts`, `logs`, `creds`, `doctor`, `init`, and `version` are registered through Spectre.Console.Cli.
- Existing `dispatch run --script <path> --computer-name <names>` callers are preserved through the compatibility parser because that syntax predates the new command tree.
- `run cmd`, `run exe`, `apply`, `push`, `hosts`, `logs`, `creds`, and `init` route through Spectre.Console.Cli and return planned-feature errors until their specific roadmap items are implemented.

Non-goals:
- No separate interactive execution engine.
- No Terminal.Gui dependency for the redesigned CLI surface.
- No implementation of PSRP/raw WinRM transport behavior in this slice.
- No YAML job execution in this slice; `apply` can return a planned/not-implemented command error until item `6.5`.

Dependencies:
- 5.1.

Definition of done:
- `dispatch --help` and `dispatch` show the new command tree.
- `dispatch run ps <script.ps1>` supports the current v1 PowerShell script execution path through the shared planner/executor.
- Existing v1 `dispatch run --script ...` callers still work through a compatibility path.
- Unsupported command surfaces are visible in help but fail with clear planned-feature messages.
- Tests cover root help, command help, validation errors, compatibility run routing, and not-yet-implemented command errors.

#### 6.1 Spectre Live Rendering And Output Modes

Objective:
Render live terminal progress correctly using Spectre.Console while preserving stable automation output modes.

Reference:
- `docs/cli-design.md` defines the terminal phase model, structured output modes, `--no-progress`, and renderer ownership rules for this roadmap item.

Scope:
- Introduce an internal run-event model and a single renderer that consumes execution/planning events.
- Bridge existing `DispatchExecutionProgress` events into renderer events without changing transport worker behavior.
- Use `Status` only for indeterminate preflight work, `Progress` only for measurable planning/preflight work, and one `LiveDisplay` dashboard for real host execution.
- Do not prompt while a live renderer is active.
- Worker tasks, transports, and planners must not write directly to `AnsiConsole`; the renderer owns terminal output during live phases.
- Implement `--no-progress`, `--no-color`, `--quiet`, `--verbose`, and `--trace` behavior where supported.
- Implement output modes `rich`, `table`, `json`, `ndjson`, and `yaml`; JSON emits one valid document and NDJSON emits one event per line without decorative rendering.
- Always print a stable final summary after live rendering ends for rich/table modes.

Current implementation note:
- Real execution uses a channel-fed Spectre `LiveDisplay` renderer over `DispatchExecutionProgress` events.
- Dry-run planning uses Spectre `Progress` when interactive and stable progress text when redirected.
- Non-dry-run planning uses Spectre `Status` when interactive and stable text when redirected.
- `--no-progress` is implemented.
- `--no-color`, `--quiet`, `--verbose`, and `--trace` are accepted for the current `run ps` path. `--no-color` disables ANSI/color for interactive Spectre planning/execution, `--quiet` suppresses rich/table non-error output while preserving structured output, and `--verbose`/`--trace` enrich current NDJSON event details without adding durable trace logs.
- `--output rich|table|json|ndjson|yaml` is implemented for the current `run ps` dry-run plan and final result paths; JSON/YAML suppress decorative rendering, and NDJSON emits one typed event per line for planning, execution start, per-target progress, and final result on the current stdout path.
- Durable `events.ndjson` run-history files and broader verbose/trace log detail remain pending under later logging work.

Non-goals:
- No static fake progress bars.
- No multiple concurrent Spectre live displays.
- No machine-readable parsing of rich terminal output.

Dependencies:
- 6.

Definition of done:
- Dry-run and execution progress visibly update while work is happening in real terminals.
- Redirected and `--no-progress` sessions avoid live widgets and emit the requested stable output mode.
- Final summaries include run ID, result counts, and log/result paths.
- Tests cover event rendering, output mode selection, and no worker direct-console writes where practical.

#### 6.2 Inventory And Target Selection

Objective:
Add the host selection model from the CLI design.

Reference:
- `docs/cli-design.md` defines the inventory option, target selectors, and precedence rules for this roadmap item.

Scope:
- Support `-i|--inventory <path>` for YAML inventories and simple text host files.
- Support `-t|--target <selector>` with `all`, group name, host name, comma-separated names, `tag:<name>`, and `file:<path>` forms.
- Support `--exclude <selector>`.
- Define precedence: CLI flag > job YAML > inventory host/group vars > user config > machine config > defaults.
- Preserve deterministic ordering, trimming, comments, blank-line skipping, and case-insensitive de-duplication from current target resolution.

Non-goals:
- No advanced selector expressions such as `web:&prod` or `web:!canary` in the first implementation.
- No dynamic discovery.

Dependencies:
- 6.

Definition of done:
- Existing `--computer-name` and `--target-file` can be represented through the new selector resolver.
- YAML and text host files resolve to stable target lists.
- Tests cover groups, hosts, simple files, excludes, and duplicate handling.

Current implementation note:
- `run ps` supports `-i|--inventory`, `-t|--target`, and `--exclude` for direct host selectors, simple text inventories, a small YAML inventory subset, groups, host names, `tag:<name>`, and `file:<path>`.
- Advanced selectors, job/config precedence, and broader YAML inventory schema behavior remain pending.

#### 6.3 Structured Run Logs And Log Commands

Objective:
Create the run-history and log-inspection surface from the CLI design.

Reference:
- `docs/cli-design.md` defines the `logs` command group and structured output expectations for this roadmap item.

Scope:
- Write run folders under `C:\ProgramData\Dispatch\logs\<yyyy>\<MM>\<dd>\<run-id>\` unless overridden.
- Record `run.json`, `plan.json`, `summary.json`, `events.ndjson`, host stdout/stderr/transcript paths where available, and collected artifacts.
- Implement `dispatch logs list|show|tail|export|retry` command stubs first, then functional readers over the durable run log format.
- Ensure credential reference names may be logged, but secret values must never be logged.

Non-goals:
- No central server or remote log store.
- No retry re-execution until the run log format is stable.

Dependencies:
- 6.1.

Definition of done:
- New runs record an event stream and summary in the documented layout.
- `logs list` and `logs show latest` can read local run history.
- Tests cover layout planning, event serialization, and summary export.

#### 6.4 Credential References

Objective:
Add the credential-reference surface without storing plaintext credentials in jobs or inventories.

Reference:
- `docs/cli-design.md` defines the `creds` command group, credential option, and CLI safety rules for this roadmap item.

Scope:
- Implement `dispatch creds add|list|test|remove` command surface.
- Store references through a provider abstraction rather than embedding plaintext in YAML.
- Allow inventories/jobs to reference `credential: <name>`.
- Add policy validation that rejects plaintext passwords in config, inventory, and job files.

Non-goals:
- No plaintext password command-line flags.
- No general-purpose secret vault in v1.
- No automatic credential delegation, CredSSP, trusted-host, or remoting policy changes.

Dependencies:
- 6.2.

Definition of done:
- Credential commands exist and clearly report whether the configured provider is available.
- YAML validation accepts reference names and rejects plaintext secret fields.
- Tests cover redaction and validation.

#### 6.5 YAML Apply And Job Model

Objective:
Support declared YAML jobs while keeping the first implementation script-first and small.

Reference:
- `docs/cli-design.md` defines `dispatch apply`, global option precedence, inventory/job expectations, and initial task vocabulary for this roadmap item.

Scope:
- Implement `dispatch apply <job.yml>`.
- Define YAML job schema version 1 with `name`, `description`, `hosts`, `transport`, `credential`, `strategy`, `defaults`, `vars`, and `tasks`.
- Initial task vocabulary: `ps`, `cmd`, `exe`, `copy`, `fetch`, `wait`, and `reboot`, with only supported task types enabled by implementation slices.
- Implement `--plan` and `--check` as distinct behaviors.
- Implement `--tags`, `--skip-tags`, `--serial`, `--concurrency`, `--yes`, `--diff`, and common output/log options as planned settings.
- Convert selected YAML tasks into the same planning/execution contracts used by ad-hoc commands.

Non-goals:
- No full Ansible compatibility.
- No roles, handlers, facts, module ecosystem, complex condition engine, or advanced expression syntax in v1.
- Unsupported task types must fail clearly during validation.

Dependencies:
- 6.2.

Definition of done:
- `dispatch apply <job.yml> --plan` resolves inventory, variables, selected tasks, batches, and transport decisions.
- Supported `ps` tasks execute through the same planner/executor model as `dispatch run ps`.
- Validation reports unsupported task types and unsafe secret fields before endpoint work.

#### 6.6 Push, Hosts, Doctor, And Init Command Surfaces

Objective:
Complete the operator command tree around execution.

Reference:
- `docs/cli-design.md` defines the `push`, `hosts`, `doctor`, and `init` command groups and their global option behavior for this roadmap item.

Scope:
- Implement `dispatch push <source> --dest <remote-path>` with `--recurse`, `--checksum`, `--overwrite`, `--backup`, `--execute`, `--execute-as`, and `--cleanup` settings.
- Implement `dispatch hosts list|test|validate|graph|vars`.
- Update `dispatch doctor` to accept `--transport psexec|psrp|winrm|auto` and check local prerequisites relevant to the selected transport.
- Implement `dispatch init config|hosts|job|all` scaffolding.

Non-goals:
- No ICMP-style `ping` command; `hosts test` tests the selected remoting path.
- No automatic endpoint remediation.

Dependencies:
- 6.2.

Definition of done:
- Command help matches the documented tree.
- Unsupported transport-specific behavior fails clearly.
- Init commands generate valid starter YAML files.

#### 6.7 CLI Safety, Policy, And Exit Codes

Objective:
Make the redesigned CLI predictable for enterprise use and automation.

Reference:
- `docs/cli-design.md` defines the safety rules, prompt restrictions, and exit-code meanings for this roadmap item.

Scope:
- Implement stable exit codes: `0` success, `1` usage/config/inventory/YAML validation error, `2` host failure, `3` unreachable host, `4` authentication/authorization failure, `5` transport initialization failure, `6` canceled, `7` plan/check policy failure, `10` internal error.
- Require explicit `--system` for LocalSystem execution and policy approval when configured.
- Require explicit PsExec fallback permission through CLI/config/inventory policy.
- Require confirmation above configured host-count thresholds unless `--yes` is supplied.
- Redact secrets from console output, logs, result JSON, CSV, dry-run/plan output, and traces.

Non-goals:
- No silent transport fallback.
- No prompting during live rendering.

Dependencies:
- 6.1.

Definition of done:
- Exit codes are covered by tests.
- Policy failures happen before endpoint work.
- Transport decisions are logged per host.

#### 6.8 Operator Diagnostics Migration

Objective:
Move local prerequisite diagnostics into the redesigned Spectre command surface.

Reference:
- `docs/cli-design.md` defines `dispatch doctor`, output mode expectations, and safety constraints for diagnostics.

Scope:
- Keep `dispatch doctor` as the primary local prerequisite command.
- Check .NET runtime, OS, PowerShell availability, PsExec path/configuration/EULA policy, WinRM client availability, log directory writability, config parseability, credential provider availability, host schema availability, current user/domain context, and policy restrictions where implemented.
- Support `dispatch doctor --transport psexec|psrp|winrm|auto`.
- Render results through Spectre tables in rich mode and stable JSON/table output when requested.
- Redact secrets and sensitive paths where needed.

Non-goals:
- No automatic environment repair.
- No endpoint fleet scan.
- No Azure login validation in MVP.

Dependencies:
- 6.6.

Definition of done:
- `dispatch doctor` returns a clear success/failure summary.
- Missing PsExec or inaccessible default paths are reported with actionable messages.
- PowerShell wrapper can expose the same behavior through `Test-Dispatch`.

#### 7. PowerShell Module Wrapper

Objective:
Provide PowerShell-friendly commands that wrap the bundled `dispatch.exe`.

Reference:
- `docs/cli-design.md` defines the CLI command contract that the PowerShell module must wrap without parsing rich terminal output.

Scope:
- Add `Dispatch.psd1` and `Dispatch.psm1`.
- Bundle `dispatch.exe` under `bin\win-x64`.
- Implement `Start-Dispatch`, `Invoke-DispatchScript`, `Invoke-DispatchJob`, and `Test-Dispatch`.
- Ensure `Start-Dispatch` passes no arguments.
- Prefer explicit JSON result path for automation functions rather than parsing rich operator output.

Non-goals:
- No binary cmdlets in MVP.
- No PowerShell repository publishing in MVP.
- No separate PowerShell execution engine.

Dependencies:
- 6.8.

Definition of done:
- Importing the module exposes the documented commands.
- `Start-Dispatch` launches the active CLI.
- `Invoke-DispatchScript` invokes `dispatch run` and returns machine-readable results.

#### 8. Source Install And Local Packaging

Objective:
Produce a tested source-install flow that builds Dispatch from a private Azure Repos clone, installs the PowerShell module locally, verifies the installation, and removes source artifacts after bootstrap installation.

Scope:
- Build single-file self-contained `dispatch.exe`.
- Assemble module package layout.
- Add `install-from-source.ps1` for use inside an existing clone.
- Add `bootstrap-install.ps1` as the primary operator flow for build/install/validate/cleanup from an existing private Azure Repos clone.
- Add `install.ps1` for installing an already assembled module package into `CurrentUser` or `AllUsers` module scopes.
- Validate the built executable, module manifest, module import, and exported commands before reporting success.
- Use a cleanup helper outside the source tree to remove the temporary source clone and bootstrap script copy after successful bootstrap installation.
- Support `-NoCleanup` for developers and troubleshooting.
- Create `Dispatch-<version>-win-x64.zip` as an optional release convenience artifact.

Non-goals:
- No MSI installer.
- No Azure Artifacts feed.
- No WinGet package.

Dependencies:
- 7.

Definition of done:
- A clean machine with Azure Repos access, Git, PowerShell, and the .NET SDK can clone the repo, run `bootstrap-install.ps1`, and run `Start-Dispatch`.
- Bootstrap installation builds the project and module, validates the installation, changes out of the source directory, invokes an external cleanup helper, and cleans up the cloned source tree including the bootstrap script itself.
- Cleanup failure is reported without uninstalling an already validated module.
- Install scripts validate the module manifest, copied EXE, import behavior, and exported commands.
- Pipeline or build script can create the optional ZIP without manual assembly.

### Remaining Transport And Post-MVP Roadmap

#### 9. PSRP Transport

Objective:
Add PowerShell Remoting Protocol execution as a first-class WinRM-based transport while preserving the same job, script execution, command execution, result, and artifact model.

Scope:
- Implement `Dispatch.Transports.Psrp` using `Microsoft.PowerShell.SDK`.
- Use `WSManConnectionInfo` and remote runspaces for PSRP-over-WSMan.
- Support `--transport psrp`.
- Support direct PowerShell command execution.
- Support direct PowerShell script execution.
- Support target configuration names such as `Microsoft.PowerShell` and later `PowerShell.7`.
- Capture output, error, warning, verbose, debug, and information streams where practical.
- Map PowerShell stream output, terminating errors, exit codes, and duration into the common target result model.
- Support current user / default Negotiate authentication first.
- Add the model surface for later explicit `PSCredential`, certificate authentication, and PSRP-over-SSH.
- Implement PSRP against the common transport result contracts.
- Preserve the script-owned external payload boundary; PSRP does not make Dispatch responsible for Blob/SAS payload retrieval.

Non-goals:
- No raw WinRM shell transport in this slice.
- No CredSSP/delegation automation by default.
- No WinRM or PowerShell remoting policy remediation.
- No Linux/macOS SSH target support.
- No endpoint-side Key Vault login requirement.

Dependencies:
- 6.1.

Definition of done:
- The same request can run through `--transport psrp`.
- PSRP results map into the same target result model as PsExec.
- Command and script execution both work through the same request/execution path.
- Tests cover runspace connection planning, configuration name handling, stream mapping, error mapping, and result serialization.
- Tests cover the common transport result contract for PSRP.
- Live validation includes at least one successful PSRP run against a user-approved WinRM-reachable Windows endpoint.

#### 9.1 Raw WinRM Transport

Objective:
Add Ansible-style raw WinRM shell/command execution as the next transport implementation priority while preserving the same job, script execution, command execution, result, and artifact model.

Scope:
- Implement `Dispatch.Transports.WinRm` as a raw WS-Management shell/command transport.
- Prefer a .NET wrapper around WinRM Client Shell API / WS-Management shell-command semantics.
- Support `--transport winrm`.
- Create remote shells and commands through WinRM shell/command semantics.
- Support direct command execution.
- Support PowerShell script execution by invoking `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <prepared script>`.
- Implement streamed/chunked script upload without requiring SMB/admin shares.
- Implement streamed/chunked artifact collection without requiring SMB/admin shares.
- Capture process stdout, stderr, exit code, duration, and failure reason.
- Support current user / default Negotiate authentication first.
- Add the model surface for later Kerberos, NTLM, Basic-over-HTTPS, certificate auth, and CredSSP where explicitly enabled.
- Keep `winrs.exe` as a diagnostic shim or test fixture only, not the primary implementation.
- Do not use PowerShell SDK runspaces in raw WinRM; that belongs to `Dispatch.Transports.Psrp`.
- Preserve the script-owned external payload boundary; raw WinRM does not make Dispatch responsible for Blob/SAS payload retrieval.

Non-goals:
- No PowerShell SDK runspace execution in this slice.
- No rich PowerShell stream mapping beyond process stdout/stderr.
- No CredSSP/delegation automation by default.
- No WinRM policy remediation.
- No Linux/macOS target support.

Dependencies:
- 6.1.

Definition of done:
- The same request can run through `--transport winrm`.
- Raw WinRM results map into the same target result model as PsExec and PSRP.
- Command and script execution both work through the same request/execution path.
- Tests cover shell/command planning, timeout classification, chunked transfer planning, stdout/stderr/exit-code mapping, and result serialization.
- Live validation includes at least one successful raw WinRM run against a user-approved WinRM-reachable Windows endpoint.

#### 10. Script-Owned Payload Documentation And Guardrails

Objective:
Document and enforce the boundary that external payload retrieval belongs to the script, while adding a narrow runtime secret handoff model for scripts that need SAS-backed payload access.

Scope:
- Document recommended patterns for scripts that download payloads from Blob storage with SAS access.
- Add a post-MVP Key Vault secret source that Dispatch can use on the admin workstation or jump box to retrieve a SAS once per job.
- Pass retrieved secrets to scripts as runtime inputs through a redacted mechanism, preferring an encrypted protected temporary secret file.
- Support a certificate/CMS-style encrypted secret file model where Dispatch encrypts the SAS before writing it over SMB and the endpoint script decrypts it locally.
- Document DPAPI machine-scope encryption as an endpoint-side option when the secret must be protected after arrival rather than before SMB transit.
- Keep examples focused on passing script arguments, environment values, or encrypted protected secret-file paths into scripts.
- Add warnings in docs/help text that scripts should not write SAS tokens to logs.
- Redact secret values from console output, logs, result JSON, CSV summaries, dry-run output, and transport command rendering.

Non-goals:
- No Dispatch-owned Blob download implementation.
- No Azure Files SMB identity framework.
- No general-purpose secret vault system.
- No endpoint-side Key Vault login requirement for the default model.
- No command-line secret passing as the preferred PsExec model.
- No SAS generation, persistence, validation, or refresh.
- No plaintext secret file as the preferred post-MVP handoff model.
- No long-lived SAS storage.
- No installer/media payload staging.

Dependencies:
- 8.

Definition of done:
- Documentation includes script-owned Blob/SAS payload examples.
- Dispatch can retrieve a configured Key Vault secret before a job and expose it to the script through an encrypted protected secret file without logging the value.
- PsExec and WinRM plans show redacted secret handoff behavior.
- Cleanup removes the remote secret file/folder after execution, with failed cleanup reported separately from script execution.
- CLI/help text does not imply Dispatch owns Blob payload retrieval.
- The roadmap keeps Blob payload orchestration out of Dispatch scope.

#### 11. Managed Execution Mode

Objective:
Add optional harness-based managed execution for stronger normalized results.

Scope:
- Add `-ExecutionMode Managed` / `--execution-mode managed`.
- Generate a minimal internal harness only when managed mode is selected.
- Standardize `stdout.txt`, `stderr.txt`, `result.json`, timeout behavior, and child process exit classification.

Non-goals:
- Managed mode is not the default.
- No script rewriting.
- No replacement for direct execution mode.

Dependencies:
- 9.

Definition of done:
- Direct mode remains unchanged.
- Managed mode produces consistent result files across PsExec, PSRP, and raw WinRM.
- Tests cover success, nonzero exit, terminating exception, and timeout classification.

#### 12. Advanced Job Features

Objective:
Extend the YAML job system after the script-first apply surface is stable.

Scope:
- Add advanced selectors and expressions such as `web:&prod` and `web:!canary`.
- Add richer task conditions beyond the first safe expression subset.
- Add reusable job includes/templates if a concrete need appears.
- Add optional JSON job input after the YAML schema has stabilized.
- Keep module schemas and examples aligned with the CLI job schema.

Non-goals:
- No full Ansible compatibility.
- No roles, handlers, facts, or external module ecosystem.
- No Ansible-style module ecosystem.

Dependencies:
- 6.5.

Definition of done:
- Advanced features are schema-versioned.
- Unsupported features fail during validation before endpoint work.
- Existing script-first YAML jobs remain compatible.

#### 13. Enterprise Distribution

Objective:
Add enterprise-grade distribution channels after the source-install and local packaging flow is stable.

Scope:
- Azure Artifacts PowerShell repository publishing backed by a NuGet feed.
- Command-line installation through `Register-PSResourceRepository` and `Install-PSResource`.
- CI package publishing through `Publish-PSResource` or equivalent NuGet package publishing.
- MSI installer.
- Optional WinGet/private package feed.
- Optional binary PowerShell cmdlets that call `Dispatch.Core` directly.

Non-goals:
- No public gallery publishing unless explicitly approved.
- No forced company repository requirement for local install.
- No endpoint agent installer.

Dependencies:
- 8.

Definition of done:
- Azure Artifacts feed can publish and install the module package as a PowerShell resource.
- Operators can install with `Install-PSResource Dispatch -Repository <DispatchFeed>`.
- MSI install/uninstall works for all-user installation.
- Existing source install and optional ZIP install remain supported.

## 12. Acceptance Criteria

- The project compiles cleanly.
- Unit tests pass.
- Request validation rejects unsupported transport/payload combinations before endpoint work starts.
- Result JSON includes the minimum stable run and per-target schema fields.
- Failures map to the common failure category enum with transport-specific details isolated in metadata.
- `dispatch` starts the active CLI.
- `dispatch run ps <script.ps1> --plan` or the compatibility dry-run path produces a complete execution plan.
- `dispatch run` can run a prepared script through PsExec against a test target or shim.
- WinRM-based endpoint execution validation must include at least one successful live raw WinRM or PSRP verification against a user-approved reachable Windows endpoint before those transports are claimed.
- PsExec validation may remain blocked when the supplied environment does not provide working `\\<device>\C$` admin-share staging, but that blocker must be recorded explicitly before any PsExec-first claim is made.
- `dispatch --help` shows the documented Spectre command tree.
- Rich terminal output uses Spectre.Console, and JSON/NDJSON output modes are non-decorative.
- Multi-target execution respects throttle limit.
- Dispatch does not own installer/media payload staging or Blob/SAS payload retrieval.
- Results are written as CSV and JSON.
- PowerShell module wrapper can launch interactive and automation flows.
- Source installer can build, install, validate, and clean up the module and bundled EXE.
- Optional ZIP installer can install the module and bundled EXE.
- No v1 core flow requires a remote launcher/harness file.

## 13. Implementation Order

1. Foundation and repository structure.
2. Job/result model.
3. Application host and configuration.
4. Request planning and dry run.
5. Target resolution.
6. Local run layout.
7. Script transfer and remote run path.
8. Script-owned external payload contract.
9. PsExec transport and direct execution.
10. Endpoint probe and execution guardrails.
11. Batch orchestration and results.
12. Artifact copy-back.
13. Spectre.Console.Cli product surface.
14. Spectre live rendering and output modes.
15. Inventory and target selection.
16. Raw WinRM transport.
17. PSRP transport.
18. Structured run logs and log commands.
19. Credential references.
20. YAML apply and job model.
21. Push, hosts, doctor, and init command surfaces.
22. CLI safety, policy, and exit codes.
23. Operator diagnostics migration.
24. PowerShell module wrapper.
25. Source install and local packaging.
26. Script-owned payload documentation and guardrails.
27. Managed execution mode.
28. Advanced job features.
29. Enterprise distribution.
