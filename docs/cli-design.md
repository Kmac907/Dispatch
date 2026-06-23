# Dispatch CLI Design

Status: draft, partially implemented. Current implementation registers the documented command tree through Spectre.Console.Cli, preserves `dispatch run --script ...` through a compatibility parser, renders real execution through a Spectre `LiveDisplay`, supports initial structured output modes, current-path output-control flags, current-path NDJSON stdout event streaming, a durable `Admin\events.ndjson` event stream plus reduced `Admin\results.json` summary for real runs, functional `dispatch logs list`, `dispatch logs show latest`, `dispatch logs tail`, `dispatch logs export [run-id|latest] --dest <path>`, and read-only `dispatch logs retry [run-id|latest]` readers/exports over the local run-history layout, initial inventory/target selectors for `run ps`, initial inventory transport precedence from YAML defaults/group vars/host vars including inline transport maps in the current supported subset, YAML inventory credential-reference validation for `credential: <name>` with plaintext secret-field rejection, direct `run ps|cmd|exe --credential <name>` reference override selection and provider validation before planning, prompt/DPAPI-file/Windows-Credential-Manager/Azure-Key-Vault runtime credential resolution and handoff for real PSRP and raw WinRM execution, a credential command surface with default unavailable-provider behavior plus file-backed reference metadata storage when configured, script-first `dispatch apply <job.yml>` plan and execution paths for one `ps` task, a completed PSRP transport for the current roadmap scope, and a completed raw WinRM transport. Multi-task YAML job behavior, PowerShell-wrapper PSCredential handoff, and push/hosts/init behavior remain roadmap work.

This document records the active CLI design that supersedes the earlier Terminal.Gui command-center direction. `docs/plan.md` remains the roadmap source of truth; this file gives the command and output contract in one place.

## Product Shape

Dispatch is a Windows-native C# automation runner for Windows hosts. The product design covers ad-hoc execution, declared jobs, host inventories, structured logs, credential references, and explicit transports. The credential store and provider-resolution contract is defined in `docs/credential-store-plan.md`. Current implementation status is tracked in `docs/plan.md` and `workflow/build/implementation-plan.md`.

Current roadmap priority is `6.4` Credential References. PsExec, raw WinRM, and PSRP are the three current transport paths today. PSRP now has shared-path slices where `psrp` requests validate for script and command payloads, the host registers PSRP transport services, preflight performs DNS plus default WSMan port reachability probing, direct command payloads execute through a PowerShell remote runspace, PowerShell scripts execute through a runspace-backed remote script path, declared/default artifact folders collect back over the remoting channel, structured `streamRecords` capture PSRP warning/verbose/debug/information/error output for the current transport path, optional shared execution-context configuration-name selection defaults to `Microsoft.PowerShell`, and the shared execution context explicitly includes later Kerberos and CredSSP placeholders while the implemented runtime still accepts only WSMan plus current-user `Default` or `Negotiate`. Unsupported future auth/connection selections fail during request validation instead of being ignored. Current PSRP failures classify timeout conditions into `TimedOut` and preserve consistent transport metadata on both success and failure, and elevated live validation for both current execution paths succeeds against locally approved validation hosts. Raw WinRM now covers request validation, planning, DI registration, endpoint reachability probes, chunked script-transfer preparation planning, remote script upload without SMB/admin shares, raw-shell-backed PowerShell script execution, direct command execution, shell-open authentication/authorization/transport classification, timeout classification in the shared result model, and artifact collection over the WinRM channel, with successful elevated live validation against locally approved validation hosts.

Live endpoint validation for the CLI only requires one user-approved reachable Windows endpoint for the relevant transport/slice. Additional supplied devices that are offline, powered down, disconnected, or unreachable are lab availability issues and should be reported separately; they do not invalidate a live test that succeeded on another approved reachable endpoint. The detailed validation policy is in `docs/testing-and-validation.md`.

The operator model is:

```text
YAML owns orchestration. The CLI owns selection, preview, execution, logging, and reporting.
```

## Command Tree

```text
dispatch apply <job.yml>
dispatch run ps <script.ps1>
dispatch run cmd <command>
dispatch run exe <path>
dispatch push <source> --dest <remote-path>
dispatch hosts list|test|validate|graph|vars
dispatch logs list|show|tail|export|retry
dispatch creds add|list|test|remove
dispatch doctor
dispatch init job|hosts|config|all
dispatch version
```

Transports are not top-level commands. They are selected with:

```text
--transport auto|psrp|winrm|psexec
```

Accepted transport names are broader than the currently completed executors. `psrp` is now accepted by request validation, registered into the host, covered by a preflight WSMan reachability probe, can execute direct command payloads through a remote runspace, can execute script payloads through a runspace-backed remote script path, can collect declared/default artifact folders back over the remoting channel with measurable download progress when the remote archive size is known, carries structured `streamRecords` for current PSRP warning/verbose/debug/information/error output, carries optional shared execution-context configuration-name selection that defaults to `Microsoft.PowerShell`, and carries a shared auth/connection model surface that explicitly includes later Kerberos and CredSSP placeholders while the implemented runtime still validates only WSMan plus current-user `Default` or `Negotiate`. Unsupported future auth/connection selections fail during request validation, and current PSRP execution classifies timeout failures into `TimedOut` and keeps `configurationName` / `connectionKind` / `authentication` / `scheme` / `port` metadata consistent on both success and failure results. Elevated live validation succeeds for both current execution paths against locally approved validation hosts. `winrm` is now a completed execution path that can validate requests, build plans, probe endpoint reachability, upload a prepared script, execute that prepared PowerShell script, execute direct command payloads, classify shell timeouts plus shell-open authentication/authorization/transport failures into the shared result model, and collect declared/default artifact folders. Elevated live validation now succeeds from the current admin workstation against locally approved validation hosts.

## PowerShell Wrapper Surface

The PowerShell module is a wrapper over the command tree, not a separate shell or "active CLI" mode. The bundled `dispatch.exe` remains the canonical entrypoint for root help and direct CLI use, and wrapper functions should map one-for-one onto command-oriented operations.

Planned initial wrapper surface:

```text
Invoke-DispatchPowerShell  # calls dispatch.exe run ps ...
Invoke-DispatchCommand     # calls dispatch.exe run cmd ...
Invoke-DispatchExecutable  # calls dispatch.exe run exe ...
Invoke-DispatchJob         # calls dispatch.exe apply ...
Test-Dispatch              # calls dispatch.exe doctor
Get-DispatchVersion        # calls dispatch.exe version
```

The wrapper should not invent a persistent interactive shell command such as `Start-Dispatch`. Operators who want CLI help or the raw command tree should use `dispatch --help` and the documented `dispatch` subcommands directly.

## Global Options

These options should be consistent across host-targeting commands as implementation reaches each command surface:

```text
-i, --inventory <path>
-t, --target <selector>
--exclude <selector>
--transport <auto|psrp|winrm|psexec>
--credential <name>
--concurrency <n>
--timeout <duration>
--config <path>
--log-dir <path>
--run-id <id>
--output <rich|table|json|ndjson|yaml>
--no-color
--no-progress
--quiet
--verbose
--trace
```

Transport selection precedence:

```text
CLI --transport (except auto) > job.transport > inventory transport policy > explicit --config > ambient bound Dispatch config > application defaults
```

Runtime variable precedence:

```text
job.vars only in v1
```

`--transport auto` is a deliberate fall-through request, not a forcing override. Inventory transport policy resolves in this order: host `vars.transport`, then inherited group `vars.transport`, then `defaults.transport`.

Inventory vars are not a general runtime-variable source. They are target metadata, and in the current implementation they are only used for inventory transport policy. For v1 job work, `job.vars` is an inline runtime/task-input bag and does not merge inventory vars into task variables. Job YAML precedence belongs to roadmap item `6.5`. For the current non-job `run ps` path, `6.2` only owns:

- CLI defaults/overrides for `inventory`, `target`, `exclude`, and `transport`
- inventory transport policy from the supported YAML subset
- explicit `--config` defaults
- ambient bound `Dispatch` config defaults
- application defaults

Credential-reference precedence belongs to roadmap item `6.4` and the canonical model is `docs/credential-store-plan.md`. The current `6.4` implementation wires `dispatch creds add|list|test|remove` to a credential provider abstraction, reports provider availability, loads the global YAML config at `C:\ProgramData\Dispatch\config.yml` when present, accepts explicit YAML `--config <path>` files on the current `run ps|cmd|exe` and `apply --plan` paths, reads config-defined `credentials:<name>` entries for credential commands, accepts `credential: <name>` reference names in the supported YAML inventory subset and current job subset, and accepts direct `run ps|cmd|exe --credential <name>` plus `apply --plan --credential <name>` overrides. Config-defined credential references now validate provider-specific required metadata for `prompt`, `pscredential`, `dpapi_file`, `windows_credential_manager`, and `azure_keyvault` before credential commands or direct CLI overrides accept them. Direct CLI credential overrides are validated through the configured provider before endpoint planning and replace inventory/job-selected references for all selected targets. For real PSRP execution, `provider: prompt`, `provider: dpapi_file`, `provider: windows_credential_manager`, and `provider: azure_keyvault` references are resolved after planning and before live rendering or endpoint work; the resolved secret stays in memory, is passed to PSRP as a `PSCredential` for command, script, and artifact sessions, and is disposed after the run. For real raw WinRM execution, the same providers resolve after planning and before live rendering or endpoint work; the resolved secret stays in memory, is passed into WSMan connection options for upload, execution, and artifact-download shells, and is disposed after the run. `--plan` / dry-run paths never prompt, decrypt DPAPI files, read Windows Credential Manager targets, or read Key Vault secrets. `dpapi_file` enrollment prompts for password plus confirmation, writes a Windows DPAPI CurrentUser-protected file to the configured `path`, disables inherited ACLs, grants file access only to the current Windows user, local Administrators, and LocalSystem, refuses overwrite unless `--force` is supplied, verifies decryptability through `creds test`, and deletes the protected file through `creds remove` while leaving the config reference intact. `windows_credential_manager` enrollment prompts for password plus confirmation, writes a generic Windows Credential Manager entry to the configured `target`, refuses overwrite unless `--force` is supplied, verifies readability through `creds test`, and deletes the configured target through `creds remove` while leaving the config reference intact. `azure_keyvault` add/test validates the configured Key Vault secret reference through the Azure SDK without storing the endpoint password locally. Inventory credential references resolve in this order: host `credential` or host `vars.credential`, then inherited group `vars.credential`, then `defaults.credential`. Conflicting inherited group references fail validation. The default provider is intentionally unavailable and does not store plaintext secrets. Configuring `Dispatch:CredentialProvider` / `dispatch.default_credential_provider` as `file` or `local` enables a JSON reference catalog at `Dispatch:CredentialStorePath` / `dispatch.credential_store_path`, defaulting to `C:\ProgramData\Dispatch\Credentials\references.json`; this catalog stores reference names and optional username metadata only. Current inventory/job validation rejects plaintext secret-like fields such as `password`, `secret`, `token`, `sas`, `sasToken`, and fields ending in `Password`, `Secret`, or `Token`; YAML config loading rejects direct plaintext secret keys such as `password`, `secret`, `token`, and `sas`. PSCredential wrapper handoff remains tied to the later PowerShell module wrapper entry point. PsExec explicit credential handoff remains intentionally blocked by the current no-plaintext `psexec -u/-p` boundary.

Credential commands do not accept plaintext password flags. `creds add` accepts a reference name and optional username metadata only; unsupported extra arguments are rejected instead of being ignored.

## Output Model

Dispatch uses Spectre.Console for human terminal output and durable JSON/NDJSON/YAML files or streams for automation.

The terminal has four phases:

1. Parse and validate: stable text and tables.
2. Preflight: `Status` for indeterminate work and `Progress` for measurable work.
3. Execution: one `LiveDisplay` dashboard driven by internal run events.
4. Final summary: stable post-live result summary with run ID, counts, and log paths.

Workers must not write directly to `AnsiConsole`. They emit events. A single renderer consumes events and owns live terminal output.

Current execution rendering uses `DispatchExecutionProgress` events, a channel-fed renderer, and one Spectre `LiveDisplay` for live runs. Planning uses Spectre `Status` or `Progress` only during active work. Redirected sessions and `--no-progress` use stable non-live rendering.

The live dashboard contract is:

- one `LiveDisplay` surface only
- refresh on new execution events and on a one-second heartbeat while the run is active
- a summary row with aggregate running/succeeded/failed/pending counts and run elapsed time
- one measurable completion bar based only on completed targets versus total targets
- per-target rows that show:
  - target name
  - status (`Pending`, `Running`, `Succeeded`, `Failed`, `Timed Out`, `Cancelled`)
  - current phase (`Resolving`, `Probing`, `Preparing Script`, `Executing`, `Collecting Artifacts`, `Complete`)
  - current-phase elapsed time
  - a measurable progress cell only when the renderer has a real denominator
  - exit code when available
  - detail text or failure message
- active targets ordered ahead of pending and completed targets so the operator sees active work first
- a recent-events view sourced from the same run-event stream
- the final rich/table summary, after live rendering ends, prints an `Outputs` panel that shows:
  - `Admin\results.json`
  - `Admin\events.ndjson`
  - the per-target local root pattern
  - separate `stdout.txt` and `stderr.txt` locations

Current measurable per-target progress sources are:

- WinRM script upload chunk counts and bytes during raw shell upload
- WinRM artifact download bytes when the remote archive size is known
- PSRP artifact download bytes when the remote archive size is known

The dashboard must stay honest:

- use progress bars only where progress is measurable
- do not invent per-target percentages from state transitions
- keep phase text phase-based, not pseudo-percent-based
- keep workers write-free with respect to the terminal; only the renderer updates the live widgets

## Structured Output

`--output json` emits one valid JSON document and suppresses decorative UI.

`--output ndjson` emits one stdout event per line and suppresses decorative UI for the current `run ps` path. Runs now also persist `Admin\events.ndjson` as the canonical durable event stream, while stdout NDJSON remains the live command-output stream.

Default local output set for real runs:

```text
Admin\events.ndjson
Admin\results.json
Targets\<target>\stdout.txt
Targets\<target>\stderr.txt
Targets\<target>\artifacts\...
```

Optional duplicate/export files:

```text
Admin\results.csv
Admin\dispatch.log
Targets\<target>\result.json
```

`--no-progress` disables live widgets and keeps terminal output stable for CI and redirected hosts.

## Inventory And Jobs

YAML inventory is the documented default because it supports groups, transport policy, host metadata, and credential references. Simple text host files remain supported for quick ad-hoc work.

Dispatch YAML is intended to be structured and validated, not free-form. The project contract is to define explicit schema versions, accepted top-level fields, accepted task types, selector rules, and validation errors before endpoint work starts. This is similar in spirit to Ansible inventory and playbook parsing, but narrower in scope and versioned around Dispatch's own model.

Initial selectors:

```text
all
group-name
host-name
host1,host2,host3
tag:name
file:path
```

For the current `run ps` path, a group selector expands nested YAML `groups.<group>.children` transitively, with duplicate hosts removed in first-seen order. Child-group references must resolve to defined groups, and cyclic child-group graphs fail validation before planning.

Advanced selector expressions such as `web:&prod` and `web:!canary` are not part of the initial selector set and must fail validation clearly.

Initial documented job schema direction:

```text
schemaVersion
name
description
hosts
transport
credential
strategy
defaults
vars
tasks
```

`transport` is a first-class job field. It is not part of the generic job `vars` bag and must not be accepted under `job.vars`.

`job.vars` is the planned task/runtime input map for `dispatch apply`. It is inline job data in v1, not a reference to separate vars files. The v1 design does not include Ansible-style `group_vars`, `host_vars`, `vars_files`, or `include_vars`.

Initial task vocabulary is also explicit and closed. Unsupported fields, unsupported task types, unsafe secret fields, and unsupported selector expressions must fail validation before any endpoint probe, staging, or execution begins.

Initial inventory direction is also explicit rather than arbitrary YAML. The `6.2` completion boundary for the current `run ps` path is a closed subset:

- top-level sections: `defaults`, `groups`, `hosts`
- supported fields only:
  - `defaults.transport`
  - `defaults.credential`
  - `groups.<group>.hosts`
  - `groups.<group>.children`
  - `groups.<group>.vars.transport`
  - `groups.<group>.vars.credential`
  - `hosts.<host>.credential`
  - `hosts.<host>.tags`
  - `hosts.<host>.vars.transport`
  - `hosts.<host>.vars.credential`
- supported syntax today:
  - top-level `hosts:` block-list, inline-list, mapping-form, and inline-map host entries
  - `groups.<group>.hosts` / `children` block-list, inline-list, and mapping-form entries
  - block-map or inline-map syntax for the supported `transport` and `credential` fields, including `defaults: { transport: winrm, credential: prod-admin }`, `groups.<group>.vars: { transport: psrp, credential: web-admin }`, and `hosts.<host>.vars: { transport: psexec, credential: host-admin }`
  - block-list or inline-list tags

Explicit `--config` overrides ambient config values where it supplies them, and inventory transport still overrides config/default transport when CLI transport is omitted. Defaults-only inventories are treated as YAML and fail clearly when no real hosts are selected, rather than being parsed as text host files. Unsupported inventory sections and unsupported fields inside the current subset fail validation clearly. Plaintext secret-like inventory fields fail with a credential policy validation error.

Inventory vars are host/group metadata only. In the current implemented subset they are limited to `vars.transport`, which controls transport policy, and `vars.credential`, which names a credential reference. They do not flow into task/runtime variables.

This item does not own generic host/group variable bags, runtime credential resolution, job runtime vars, or job YAML merge behavior. Those belong to later roadmap items. `6.2` is now complete unless the roadmap is explicitly amended.

Initial YAML job task vocabulary:

```text
ps
cmd
exe
copy
fetch
wait
reboot
```

Unsupported task types must fail during validation before endpoint work starts.

## Safety Rules

- Do not allow plaintext passwords in jobs, inventories, config, logs, or command-line flags.
- Do not log secrets.
- Do not enable PsExec fallback silently.
- Do not run as LocalSystem unless explicitly requested and policy allows it.
- Do not prompt while a live display is active.
- Do not write directly to the console from worker threads.
- Do not make automatic transport changes without logging the final transport per host.
- Require confirmation above configurable host-count thresholds unless `--yes` is supplied.
- Keep JSON and NDJSON non-decorative and stable.

## Exit Codes

```text
0   All selected hosts/tasks succeeded.
1   CLI usage, config, inventory, or YAML validation error.
2   One or more hosts failed.
3   One or more hosts were unreachable.
4   Authentication or authorization failure.
5   Transport initialization failure.
6   Run canceled by user.
7   Check/plan policy validation failed.
10  Internal/unhandled error.
```
