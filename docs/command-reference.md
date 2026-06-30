# Command Reference

`dispatch.exe` is the canonical command surface. The planned PowerShell module will wrap this executable and will not provide a separate execution engine.

Status legend:

- Current: implemented in the current codebase.
- Planned v1: required by the v1 roadmap but not fully implemented yet.
- Deferred: explicitly outside v1.

## Command Model At A Glance

Dispatch commands are grouped by operator intent:

| Command | Primary intent | Use it when |
| --- | --- | --- |
| `apply` | Declared job execution | The work should be repeatable, reviewable, and stored in YAML. |
| `run` | Ad-hoc execution | You want Dispatch to execute a script or command as a managed run and collect run results. |
| `push` | File placement | You need to copy a file or directory to an exact remote path, optionally executing a pushed `.ps1` afterward. |
| `hosts` | Inventory inspection | You need to understand or validate host files, selectors, graph structure, or effective host metadata. |
| `logs` | Local run-history inspection | You need to inspect, export, tail, or build a retry plan from previous Dispatch runs. |
| `creds` | Credential-reference lifecycle | You need to enroll, test, list, or remove provider-backed credential reference state. |
| `doctor` | Local prerequisite diagnostics | You need to check the admin workstation and local Dispatch configuration. |
| `init` | Starter file scaffolding | You need starter config, inventory, or job YAML in the current directory. |
| `version` | Build identity | You need the executable version/build identity for support or automation. |

`run`, `apply`, and `push --execute` can all result in remote execution, but they own different things. `run` owns an execution lifecycle and result set. `apply` owns a declared multi-task job file. `push` owns remote file placement; `--execute` is a follow-up step for a pushed single `.ps1` file.

## Root

```powershell
dispatch --help
dispatch version
```

Status: current.

- `dispatch --help` prints the registered command tree and high-level examples.
- `dispatch version` prints the CLI version/build identity available to the executable.

Use root help when discovering available command groups or checking whether the executable is wired correctly. Use `version` when recording environment details for support, release validation, or automation logs.

## Apply

```powershell
dispatch apply <job.yml> [--inventory <hosts.yml>] [--target <selector>] [--exclude <selector>] [--config <path>] [--credential <name>] [--transport auto|psrp|winrm|psexec] [--tags <tags>] [--skip-tags <tags>] [--serial <n>] [--plan|--check] [--diff] [--output rich|table|json|ndjson|yaml] [--no-color] [--no-progress] [--quiet] [-v|--verbose] [--trace]
```

Status: partial/current. Plan, check, and execution are implemented for selected multi-task script-first `ps`, scalar `cmd`, and scalar `exe` jobs in YAML order. Plan/check-only rendering is implemented for selected `copy` tasks. Additional task types remain planned v1.

Runs a declared YAML job. The v1 job model is script-first and converts supported tasks into the same planning/execution contracts used by ad-hoc commands.

Use `apply` when the operation should be reviewed, repeated, checked into source control, or composed from multiple ordered tasks. Use `run` instead for a one-off script or command where a job file would add unnecessary ceremony. Use `push` instead when the main goal is to place files at a specific remote destination.

Scalar `job.vars` entries are passed to selected `ps` tasks as named PowerShell script arguments in YAML order. Scalar `cmd` tasks run through the same command payload path as `dispatch run cmd`, and scalar `exe` tasks run through the same command payload path as `dispatch run exe`. Selected `copy` tasks are plan/check-only and use mapping syntax such as `copy: { src: .\payloads\agent.msi, dest: C:\ProgramData\Dispatch\Payloads\agent.msi, overwrite: true }` or a block with `src`, `dest`, `overwrite`, and `tags` fields. Inventory vars remain host/group metadata and do not become runtime task inputs.

Use `--target <selector>` to override `hosts` from the job file for the current run. Use `--inventory <path>` to override the configured inventory path. Use `--exclude <selector>` to remove hosts from the selected set after the job or CLI target selector is resolved.

Use `--transport <value>` to override job, inventory, and config transport policy. `--transport auto` is a fall-through value: Dispatch uses non-`auto` `job.transport`, then inventory transport policy, then config/default transport. If the selected inventory hosts resolve to conflicting transport policies and no explicit non-`auto` transport is supplied, validation fails before planning.

Omitted `--transport` and `--transport auto` must not implicitly select PsExec unless fallback policy is approved. Explicit `--transport psexec` remains the CLI opt-in. Config approval is `dispatch.allow_psexec_fallback: true`; inventory approval can be `allow_psexec_fallback: true` on defaults, group vars, host entries, or host vars where supported by the implementation. Missing approval returns policy exit code `7` before planning or endpoint work.

Use `--tags <tags>` to select `ps`, `cmd`, `exe`, and plan/check `copy` tasks when at least one task tag matches. Use `--skip-tags <tags>` to exclude supported tasks when any task tag matches. Tag values are comma-separated, and a filter that excludes every supported task fails before endpoint work. Execution runs the selected `ps`, `cmd`, and `exe` tasks in YAML order and stops after the first failed task run. When `apply` reaches task execution, its process exit code preserves the stable underlying run exit code for the executed task outcome. Real `copy` execution is rejected before endpoint planning until file transfer execution is implemented.

Use `--plan` to inspect the resolved plan. Use `--check` to validate and render the supported job subset without endpoint work. The current check mode does not simulate task side effects.

Use `--serial <n>` to override `strategy.serial` for the supported apply subset. This controls how many targets Dispatch includes in each apply batch.

Use `--no-progress` to disable live widgets, `--quiet` to suppress rich non-error output, and `--verbose` / `--trace` for more detailed NDJSON diagnostics. `--diff` is accepted as an explicit planned setting but fails before planning until the diff behavior slice is implemented.

## Run

```powershell
dispatch run ps <script.ps1> [args] --target <selector> [--inventory <hosts.yml>] [--transport psrp|winrm|psexec] [--credential <name>] [--secret name=reference]
dispatch run cmd <command> --target <selector> [--transport psrp|winrm]
dispatch run exe <path> --target <selector> [--transport psrp|winrm]
```

Status: `run ps`, `run cmd`, and `run exe` route through the shared planner/executor when the transport supports the payload. Compatibility routing for the older `dispatch run --script ...` shape remains for migration.

Use `run` for execution-first automation. Dispatch stages or prepares whatever the selected transport needs, creates a run folder, emits events, captures stdout/stderr, writes `Admin\results.json`, and records target state. The remote payload is part of an execution job, not a file deployment.

Use `push --execute` instead only when the remote file destination itself matters, such as placing `C:\Temp\Fix.ps1` and then running that exact copied file. Use `apply` instead when the same execution should live in a declared YAML job.

Common options:

- `--target`
- `--target-file`
- `--inventory`
- `--config`
- `--transport`
- `--credential`
- `--secret name=reference` for script secret handoff on `run ps`
- `--expected-exit-code`
- `--throttle` / concurrency option where implemented
- `--plan`
- `--system` for PsExec LocalSystem execution when `dispatch.allow_run_as_system: true` is set in the selected config
- `--output`
- `--no-progress`

Examples:

```powershell
dispatch run ps .\Fix.ps1 --target PC001,PC002 --transport psrp
dispatch run ps .\Fix.ps1 --inventory .\hosts.yml --target kiosks --transport psrp --credential prod-admin
dispatch run cmd whoami --target PC001 --transport winrm --output json
dispatch run cmd whoami --target PC001 --transport winrm --credential prod-admin --output json
dispatch run ps .\Fix.ps1 --target PC001 --plan --output json
```

Use `--plan` to validate inputs and inspect the selected targets, payload, transport, credential reference, and run paths before endpoint work starts.

Use `--system` only with `--transport psexec` when the selected config explicitly allows it with `dispatch.allow_run_as_system: true`. LocalSystem requests without that policy approval, or LocalSystem requests for WinRM/PSRP, fail before planning or endpoint work and return policy exit code `7`. `--run-as-system` remains accepted as a legacy/internal alias, but new automation should use `--system`.

Script secret handoff for `run ps` uses `--secret name=reference`. It is separate from `--credential`: credentials authenticate the transport, while secrets are script inputs. `name` becomes the script parameter name, so `--secret packageSas=prod-package-sas` renders as `-packageSas [redacted]` and expects the script to declare a matching parameter such as `param([string]$packageSas)`.

Current support validates the option shape, rejects duplicate names and plaintext-looking values, and renders only the redacted parameter binding in plan/dry-run output without resolving `reference`. Roadmap 10 owns real execution: Dispatch must resolve `reference` from the configured secret provider on the admin side and bind the resolved value to the script parameter through the selected transport. Secret values must stay out of ordinary command lines, console output, logs, results, traces, artifacts, and structured output.

Subcommands:

- `run ps <script.ps1>` runs a local PowerShell script file as a Dispatch-managed execution. Extra arguments after the script path are passed through as script arguments. Use this for script-first automation, installer/remediation scripts, and any workflow where results/logs matter more than leaving the script at a specific remote path.
- `run cmd <command>` runs a Windows command line through the selected command-capable transport. Use this for short checks such as `whoami`, `hostname`, or simple diagnostics. It is not a replacement for multi-step script logic.
- `run exe <path>` runs an executable command through the selected command-capable transport. Use this when invoking a known executable directly is clearer than wrapping it in PowerShell. For complex argument handling, setup logic, or output shaping, prefer `run ps`.

The compatibility shape `dispatch run --script <path> --computer-name <names>` still works for older callers, but new docs and automation should use `dispatch run ps`.

## Push

```powershell
dispatch push <source> --dest <remote-path> [--inventory <path>] [--target <selector>] [--exclude <selector>] [--transport auto|winrm|psrp] [--credential <name>] [--config <path>] [--concurrency <n>] [--overwrite] [--plan|--check] [--recurse] [--checksum] [--backup] [--execute] [--cleanup] [--output rich|table|json|ndjson|yaml] [--no-color] [--no-progress] [--quiet] [-v|--verbose] [--trace]
```

Status: partial current.

Current support: single-file push and recursive directory push over raw WinRM or PSRP, target selection, inventory selection, `--transport auto` selection through inventory/config/default transport policy, optional replacement through `--overwrite`, target-local pre-replacement backup through `--backup`, explicit push-result SHA-256 enforcement/reporting through `--checksum`, single-file `.ps1` execute-after-copy through `--execute`, post-execute script removal through `--cleanup`, and structured output. `--plan` and `--check` preview the transfer without writing. PsExec push remains planned or deferred.

Use `push` when the file or directory destination is the primary outcome. A successful push means Dispatch wrote the requested content to the requested remote path. `--execute` is intentionally secondary: it runs the uploaded single `.ps1` after transfer succeeds. If you only care about running a script and collecting results, prefer `dispatch run ps`.

Options and behavior:

- `<source>` is the local file or directory to transfer. Directory transfer requires `--recurse` and preserves relative file paths under `--dest`.
- `--dest <remote-path>` is the drive-qualified Windows path to write on each target, such as `C:\ProgramData\Dispatch\Payloads\agent.msi`.
- `--overwrite` allows replacing an existing remote file. Without it, Dispatch creates the remote file only if it does not already exist; an existing remote file fails that target instead of being replaced. For directory push, this applies independently to each file under `--dest`.
- `--backup` requires `--overwrite`. When the destination file already exists, Dispatch creates a target-local copy beside the destination before replacing it and reports the backup path in result metadata. If the destination file does not exist, the push succeeds without creating a backup.
- `--checksum` requires the selected transfer transport to include SHA-256 metadata in the push result and fails the target if the reported remote hash does not match the local source hash. The current raw WinRM and PSRP upload clients already compare the remote SHA-256 before reporting upload success; this flag makes that verification explicit in push output and failure policy.
- `--execute` runs the uploaded file after a successful upload when the source is a single `.ps1` file. The current implementation invokes `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <remote-path>` through the selected WinRM or PSRP transport, records execution metadata in the push result, and fails the target if execution fails or exits with a nonzero code. Directory execution, non-PowerShell payload execution, and argument passing remain later push work.
- `--cleanup` requires `--execute` and removes the uploaded `.ps1` file after successful execution. Cleanup failures are reported separately as `cleanupFailed` target failures with cleanup metadata; Dispatch does not run cleanup when upload or execution fails.
- `--plan` and `--check` resolve targets and render the planned transfer without opening a remote shell or writing files.
- `--transport winrm` and `--transport psrp` are the current real push transports. Omitted transport or `--transport auto` follows inventory transport policy first, then configured/default transport; the resolved transport must be WinRM or PSRP for push. If omitted/auto resolution would select PsExec, fallback approval is still required before Dispatch rejects PsExec push as unsupported. PsExec push remains deferred unless its SMB/admin-share staging boundary is explicitly reopened.

## Hosts

```powershell
dispatch hosts list --inventory <hosts.yml> [--output rich|table|json|ndjson|yaml]
dispatch hosts test --inventory <hosts.yml> --target <selector> [--exclude <selector>] [--transport psrp|winrm|psexec|auto] [--output rich|table|json|ndjson|yaml]
dispatch hosts validate --inventory <hosts.yml> [--output rich|table|json|ndjson|yaml]
dispatch hosts graph --inventory <hosts.yml> [--output rich|table|json|ndjson|yaml]
dispatch hosts vars --inventory <hosts.yml> --target <host> [--output rich|table|json|ndjson|yaml]
```

Status: current for the supported v1 inventory subset. `hosts list`, `hosts validate`, `hosts graph`, and `hosts vars` are implemented as local inventory inspection commands. `hosts test` is implemented as transport endpoint probing for selected inventory targets.

Use `hosts` commands before execution when you need confidence in inventory structure or target selection. These commands are inspection/validation tools, not execution commands and not endpoint remediation.

Current subcommands:

- `hosts list` reads an inventory and prints the hosts Dispatch can resolve, including group membership, effective inventory transport policy, credential reference name, and source location where available. Use it to confirm names and inherited metadata before running work. It does not contact endpoints.
- `hosts test` resolves the required `--target <selector>` against the inventory, applies optional `--exclude`, resolves the effective transport from `--transport`, inventory policy, or configuration defaults, and runs the matching Dispatch transport endpoint probe for each selected host. It does not execute commands, copy files, remediate endpoints, or perform ICMP ping. Endpoint-probe results use the stable result exit-code contract. Use it to prove the chosen remoting path is reachable enough for Dispatch before running work.
- `hosts validate` validates inventory syntax/schema using the same parser and supported metadata fields as current `run`, `apply`, and `push` target resolution. It reports unsupported fields, unsupported transports, plaintext-looking secret fields, group cycles, and conflicting inherited metadata before endpoint work. Use it in pull requests or preflight checks for inventory files.
- `hosts graph` reads the inventory and prints direct group-to-host and group-to-child relationships, ungrouped hosts, group transport policy, and group credential reference names where configured. Use it when nested groups make target selection hard to reason about. It does not contact endpoints.
- `hosts vars` requires `--target <host>` and prints the effective metadata Dispatch derives for that host, including inventory source, group membership, effective transport policy, and credential reference name. Use it to understand the resolved values for a single target before running work. It does not contact endpoints.

## Logs

```powershell
dispatch logs list
dispatch logs show latest
dispatch logs tail latest --count 50
dispatch logs export latest --dest .\exports
dispatch logs retry latest
```

Status: current.

Reads local run history from `Admin\results.json` and `Admin\events.ndjson`.

Use `logs` after a Dispatch run has already happened. These commands read local files; they do not contact endpoints and they do not start new remote work.

Subcommands:

- `logs list` enumerates local runs under the configured run root and shows reduced run summaries. Use it to find a run ID or confirm the latest run.
- `logs show <run-id|latest>` prints a selected run summary, target outcomes, failure categories, and output file locations. Use it for a quick human review of one run.
- `logs tail <run-id|latest> --count <n>` reads the durable NDJSON event stream and prints the latest events. Use it when you need recent phase/progress/error events without opening the event file manually.
- `logs export <run-id|latest> --dest <path>` copies the selected run's durable summary/event data to an export folder and generates a CSV summary where available. Use it for handoff, ticket attachment, or offline review.
- `logs retry <run-id|latest>` builds a read-only retry plan for failed, timed-out, or cancelled targets. It does not rerun anything. Use it to see which targets would be retried and what command shape can be reconstructed.

## Credentials

```powershell
dispatch creds list
dispatch creds add <name> [--force]
dispatch creds test <name>
dispatch creds remove <name>
```

Status: current for config-defined prompt, DPAPI file, Windows Credential Manager, and Azure Key Vault provider behavior on PSRP and raw WinRM credential resolution. PowerShell-wrapper `PSCredential` handoff remains planned for the Roadmap `7` wrapper slice: module commands will select configured references with `-CredentialName`, use optional supplied `-Credential <PSCredential>`, or prompt with `Get-Credential` for `provider: pscredential` when `-Credential` is omitted. Direct `dispatch.exe` must continue to reject `provider: pscredential` without protected wrapper handoff. PsExec explicit password handoff remains intentionally unsupported while the v1 PsExec boundary forbids plaintext `psexec -u/-p` password passing.

Credential names are references from the loaded global YAML config. Resolved passwords are never command-line arguments.

Use `creds` to manage provider-backed local credential state for references that are already defined in config. These commands do not add plaintext passwords to YAML, and `remove` does not delete the config entry itself.

Subcommands:

- `creds list` shows configured credential references and provider metadata that is safe to display. It does not reveal passwords or secret values.
- `creds add <name>` enrolls or validates local provider state for a configured reference. For `dpapi_file` and `windows_credential_manager`, it prompts for a password and stores it in the configured protected local store. For `prompt`, it records that runtime prompting is expected. For `azure_keyvault`, it validates Key Vault access and secret readability without storing a local password.
- `creds add <name> --force` allows replacement when the configured provider already has local state, such as an existing DPAPI file or Windows Credential Manager target.
- `creds test <name>` verifies the configured provider can resolve or validate the reference. For prompt-style references, this proves metadata and provider availability rather than silently storing a password.
- `creds remove <name>` removes local provider state where that provider owns local state. It does not delete the reference definition from `config.yml` and does not delete Azure Key Vault secrets.

Examples:

```powershell
dispatch creds list
dispatch creds add helpdesk-local
dispatch creds test kv-prod-admin
dispatch run ps .\Fix.ps1 --target PC001 --transport psrp --credential prod-admin
dispatch run cmd whoami --target PC001 --transport winrm --credential prod-admin
```

## Doctor

```powershell
dispatch doctor [--transport auto|psexec|psrp|winrm]
```

Status: current for local transport-scoped prerequisite checks. Structured `doctor` output modes and broader diagnostics remain planned v1.

`doctor` reports local readiness. It does not remediate endpoints.

Current diagnostics are local prerequisite checks only. Omitted `--transport` or `--transport auto` runs shared checks plus local checks for the current transport set and can report PsExec readiness, but this does not approve PsExec fallback for host-targeting commands. `--transport psexec` includes the configured PsExec path and local admin-token checks. `--transport psrp` and `--transport winrm` include shared local checks and WinRM client availability, but they do not fail solely because PsExec is missing.

Use `doctor` when validating the admin workstation or explaining why Dispatch cannot start a local workflow. Use `hosts test` for endpoint-specific transport probes; `doctor` is not a fleet scan.

## Init

```powershell
dispatch init config
dispatch init hosts
dispatch init job
dispatch init all
```

Status: current.

Scaffolds starter YAML files in the current directory: `config.yml`, `hosts.yml`, and `job.yml`. `init all` creates all three files. Init refuses to overwrite an existing starter file.

Use `init` to create examples that are valid enough to edit, not to discover an existing environment. Init writes only to the current directory and stops before overwriting existing files.

Subcommands:

- `init config` creates `config.yml` with starter Dispatch defaults and a prompt-based credential reference example.
- `init hosts` creates `hosts.yml` with a small starter inventory group and host entries.
- `init job` creates `job.yml` with a starter declared job that targets the starter inventory group.
- `init all` creates all three starter files after first checking that none of the target paths already exists.

## Output Modes

- `rich` - Spectre.Console operator UI.
- `table` - concise human-readable text/table output.
- `json` - stable automation document.
- `ndjson` - one event per line where supported.
- `yaml` - stable YAML document where supported.

Automation should prefer result files or structured output modes instead of parsing rich terminal output.

## Exit Behavior

Dispatch returns success only when command validation succeeds and the selected targets complete according to the expected exit-code policy. For completed `dispatch run` execution results, completed `dispatch apply` execution of supported `ps`, `cmd`, and `exe` tasks, completed `dispatch push` transfer/execute results, and `dispatch hosts test` endpoint-probe results, process exit codes follow the stable result mapping: `0` success, `2` host execution failure or unexpected exit code, `3` probe failure or timed-out target, `4` authentication or authorization failure, `5` transport unavailable, `6` cancelled, and `10` internal error. Usage, configuration, inventory, YAML, planning, and local lifecycle/inspection command failures return `1`. Policy failures, including current `dispatch run` LocalSystem policy failures and `dispatch run`/`dispatch apply` PsExec fallback approval failures, return `7` before planning or endpoint work. After this slice, remaining Roadmap `6.7` work is redaction validation. Automation should read `Admin\results.json` for run/apply/push per-target execution detail and use structured `hosts test` output for endpoint-probe detail.
