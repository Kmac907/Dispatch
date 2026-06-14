# Dispatch Plan

## 1. Product Goal

Dispatch is a Windows-native, push-style remote script execution tool written in C#/.NET and packaged as a Windows single-file executable with an optional PowerShell module wrapper.

Dispatch lets an administrator take a script or command, run it on one or more Windows endpoints through selectable transports such as PsExec, PSRP, or raw WinRM, collect logs/results, and report clearly. It is not an Ansible clone. It starts as a job-based script orchestration tool, not a package manager, playbook engine, or full configuration-management system.

## 2. Scope

### v1 must ship

- `dispatch.exe` for Windows `win-x64`.
- Active CLI when `dispatch` is run with no arguments.
- Automation CLI with `dispatch run`.
- Target input from `--computer-name` and `--target-file`.
- PsExec transport.
- Direct PowerShell script execution by default.
- Script transfer/preparation under `C:\ProgramData\Dispatch`.
- Throttled multi-target execution.
- Expected exit code handling, default `[0]`, with installer-friendly support for `[0,3010]`.
- Local run folder with admin logs, CSV summary, JSON summary, and per-target outputs.
- PowerShell module that wraps the bundled EXE.
- Private Azure Repos source install flow: `git clone`, build, install module, validate, and clean up source.
- Local install script deployment for `CurrentUser` and `AllUsers` module scopes.

### v1 should ship if feasible

- Dry-run planning output for all local inputs.
- `dispatch doctor` / `Test-Dispatch` prerequisite checks.
- Concise live terminal status with non-interactive fallback output.
- Basic artifact copy-back rules.
- Release ZIP packaging as a convenience artifact.

### Out of scope for v1

- PSRP transport.
- Raw WinRM transport.
- Installer/media payload staging.
- Azure Blob payload download, SAS generation, or SAS management.
- Azure Key Vault secret retrieval or runtime secret handoff.
- Dispatch-managed credential, SAS, Key Vault, or runtime secret handoff for any transport.
- Managed/harness execution mode.
- Retry policy beyond basic failure reporting.
- YAML/JSON job manifests.
- MSI installer.
- Azure Artifacts publishing.
- Binary PowerShell cmdlets.

## 3. Non-Goals

- No Ansible-style playbook engine.
- No permanent endpoint agent.
- No Azure Files identity/auth framework.
- No CredSSP/delegation automation.
- No secret vault system.
- No Linux/macOS target support.
- No full software inventory/configuration-management model.
- No installer/media payload staging in v1.
- No Azure Blob download/SAS orchestration in v1; scripts own their own external payload access.
- No remote launcher/harness requirement in v1.

## 4. Architecture

### Runtime model

- `Dispatch.Core` owns planning, target normalization, script transfer/preparation, orchestration, result models, logging abstractions, artifact collection, and transport interfaces.
- `Dispatch.Transports.PsExec` owns PsExec command construction and captured process execution.
- `Dispatch.Transports.Psrp` is a post-MVP transport using PowerShell SDK remote runspaces and the PowerShell Remoting Protocol.
- `Dispatch.Transports.WinRm` is a post-MVP raw WinRM transport using WS-Management shell/command semantics.
- `Dispatch.Cli` owns `dispatch.exe`, interactive prompts, automation commands, and console rendering.
- `Dispatch.PowerShell` owns wrapper functions such as `Start-Dispatch`, `Invoke-DispatchScript`, `Invoke-DispatchJob`, and `Test-Dispatch`.

### Technology choices

- Language: C#.
- Platform: .NET.
- Initial runtime identifier: `win-x64`.
- Packaging target: single-file, self-contained Windows executable.
- CLI parser: `System.CommandLine`.
- Interactive console UX: `Spectre.Console`.
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
psexec + ScriptPayload   = v1 supported
psexec + CommandPayload  = modeled; post-MVP unless explicitly enabled
psrp   + ScriptPayload   = post-MVP
psrp   + CommandPayload  = post-MVP
winrm  + ScriptPayload   = post-MVP
winrm  + CommandPayload  = post-MVP
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

Result JSON is the durable contract for the CLI, PowerShell module, automation, and later package feeds. CSV is a flattened operator-friendly subset.

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

PsExec is the v1 transport. It uses SMB/admin-share file preparation and PsExec process execution from the admin workstation.

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

PSRP is the preferred post-MVP remoting transport for PowerShell-native execution. It uses PowerShell SDK remote runspaces through `WSManConnectionInfo` for PSRP-over-WSMan, with an optional future `SSHConnectionInfo` mode for PSRP-over-SSH.

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

- v1 has no PSRP transport and no supported PSRP SAS token handoff.
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

Raw WinRM is a separate post-MVP command/shell transport for environments that want Ansible-style WinRM behavior without depending on PowerShell runspace semantics. It uses WS-Management shell/command operations and maps process stdout, stderr, and exit code into the common Dispatch result model.

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

- v1 has no raw WinRM transport and no supported raw WinRM SAS token handoff.
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
- Print concise terminal summary.

Non-goals:
- No retry policy.
- No timeout/cancellation beyond basic process completion behavior unless already trivial.
- No full-screen TUI.

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
Provide both automation and interactive command surfaces over the same core request/execution path.

Scope:
- Implement `dispatch run`.
- Implement `dispatch doctor`.
- Run active interactive CLI when no arguments are supplied.
- Use `System.CommandLine` for command parsing/help.
- Use `Spectre.Console` for prompts, confirmations, tables, progress/status output, and readable terminal summaries.
- Fall back to simple append-only output when interactive rendering is unavailable.

Non-goals:
- No GUI.
- No full-screen terminal application.
- No separate interactive execution engine.

Dependencies:
- 5.1.

Definition of done:
- `dispatch` guides the user through script, targets, transport, run context, throttle, dry-run, and confirmation.
- `dispatch run` supports non-interactive automation for v1 PowerShell script execution; command payloads remain modeled and rejected until a post-MVP command execution slice explicitly enables them.
- Both modes create the same request model and call the same core planner/executor.

#### 6.1 Operator Diagnostics

Objective:
Give operators a quick way to validate local prerequisites and common configuration problems.

Scope:
- Implement `dispatch doctor`.
- Check OS, PowerShell availability, PsExec path/configuration, default output path access, and basic admin context indicators.
- Redact secrets and sensitive paths where needed.

Non-goals:
- No automatic environment repair.
- No endpoint fleet scan.
- No Azure login validation in MVP.

Dependencies:
- 6.

Definition of done:
- `dispatch doctor` returns a clear success/failure summary.
- Missing PsExec or inaccessible default paths are reported with actionable messages.
- PowerShell wrapper can expose the same behavior through `Test-Dispatch`.

#### 7. PowerShell Module Wrapper

Objective:
Provide PowerShell-friendly commands that wrap the bundled `dispatch.exe`.

Scope:
- Add `Dispatch.psd1` and `Dispatch.psm1`.
- Bundle `dispatch.exe` under `bin\win-x64`.
- Implement `Start-Dispatch`, `Invoke-DispatchScript`, `Invoke-DispatchJob`, and `Test-Dispatch`.
- Ensure `Start-Dispatch` passes no arguments.
- Prefer explicit JSON result path for automation functions rather than parsing live console output.

Non-goals:
- No binary cmdlets in MVP.
- No PowerShell repository publishing in MVP.
- No separate PowerShell execution engine.

Dependencies:
- 6.1.

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

### Post-MVP Roadmap

#### 9. PSRP Transport

Objective:
Add PowerShell Remoting Protocol execution while preserving the same job, script execution, command execution, result, and artifact model.

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
- 8.

Definition of done:
- The same request can run through `--transport psrp`.
- PSRP results map into the same target result model as PsExec.
- Command and script execution both work through the same request/execution path.
- Tests cover runspace connection planning, configuration name handling, stream mapping, error mapping, and result serialization.
- Tests cover the common transport result contract for PSRP.

#### 9.1 Raw WinRM Transport

Objective:
Add Ansible-style raw WinRM shell/command execution while preserving the same job, script execution, command execution, result, and artifact model.

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
- 9.

Definition of done:
- The same request can run through `--transport winrm`.
- Raw WinRM results map into the same target result model as PsExec and PSRP.
- Command and script execution both work through the same request/execution path.
- Tests cover shell/command planning, timeout classification, chunked transfer planning, stdout/stderr/exit-code mapping, and result serialization.

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

#### 12. Job Manifest

Objective:
Allow repeatable jobs to be described in YAML or JSON without becoming a playbook engine.

Scope:
- Define minimal job manifest schema for targets, transport, script, script arguments, execution context, expected exit codes, and artifacts.
- Add `dispatch run --job <path>`.
- Add `Invoke-DispatchJob`.
- Add schema file under module `schemas`.

Non-goals:
- No task graph.
- No conditionals, loops, facts, handlers, roles, or idempotency framework.
- No Ansible-style module ecosystem.

Dependencies:
- 8.

Definition of done:
- A manifest can run the same job as CLI parameters.
- Schema validation reports actionable errors.
- Manifest and CLI inputs converge into the same `DispatchRequest` model.

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
- `dispatch run --dry-run` produces a complete execution plan.
- `dispatch run` can run a prepared script through PsExec against a test target or shim.
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
13. CLI product surface.
14. Operator diagnostics.
15. PowerShell module wrapper.
16. Source install and local packaging.
17. PSRP transport.
18. Raw WinRM transport.
19. Script-owned payload documentation and guardrails.
20. Managed execution mode.
21. Job manifest.
22. Enterprise distribution.
