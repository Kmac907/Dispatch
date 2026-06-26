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

Use `--tags <tags>` to select `ps`, `cmd`, `exe`, and plan/check `copy` tasks when at least one task tag matches. Use `--skip-tags <tags>` to exclude supported tasks when any task tag matches. Tag values are comma-separated, and a filter that excludes every supported task fails before endpoint work. Execution runs the selected `ps`, `cmd`, and `exe` tasks in YAML order and stops after the first failed task run. Real `copy` execution is rejected before endpoint planning until file transfer execution is implemented.

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
- `--secret name=reference` for planned script secret handoff on `run ps`
- `--expected-exit-code`
- `--throttle` / concurrency option where implemented
- `--plan`
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

Script secret handoff is planned for `run ps` as `--secret name=reference`. It is separate from `--credential`: credentials authenticate the transport, while secrets are script inputs. The initial boundary is plan/dry-run validation and redacted rendering of the default remote secret-file path under the run root `secrets\` folder. Secret values must not appear in command lines, logs, results, or structured output. Real protected remote staging and cleanup are later work.

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
- `--transport winrm` and `--transport psrp` are the current real push transports. Omitted transport or `--transport auto` follows inventory transport policy first, then configured/default transport; the resolved transport must be WinRM or PSRP for push. PsExec push remains deferred unless its SMB/admin-share staging boundary is explicitly reopened.

## Hosts

```powershell
dispatch hosts list --inventory <hosts.yml>
dispatch hosts test --inventory <hosts.yml> --target <selector> [--transport psrp|winrm|psexec|auto]
dispatch hosts validate --inventory <hosts.yml>
dispatch hosts graph --inventory <hosts.yml>
dispatch hosts vars --inventory <hosts.yml> --target <host>
```

Status: planned v1 command group. Inventory parsing and selector behavior are partially implemented on current run paths.

Use `hosts` commands before execution when you need confidence in inventory structure or target selection. These commands are planned as inspection/validation tools, not as execution commands and not as endpoint remediation.

Planned subcommands:

- `hosts list` will read an inventory and print the hosts Dispatch can resolve, including group membership where available. Use it to confirm names and selector expansion before running work.
- `hosts test` will test the selected remoting path for matching hosts. It is not an ICMP ping command and will not remediate endpoints. Use it to prove the chosen transport path is reachable enough for Dispatch.
- `hosts validate` will validate inventory syntax/schema without running endpoint work. Use it in pull requests or preflight checks for inventory files.
- `hosts graph` will show group/child relationships so nested inventory structure is easier to inspect. Use it when group inheritance or child groups make selection hard to reason about.
- `hosts vars` will show the effective host metadata Dispatch can derive for one target, including supported transport and credential reference fields. Use it to understand precedence across defaults, groups, host entries, config, and CLI overrides.

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

Status: current for config-defined prompt, DPAPI file, Windows Credential Manager, and Azure Key Vault provider behavior on PSRP and raw WinRM credential resolution. PowerShell-wrapper `PSCredential` handoff remains planned for the wrapper slice. PsExec explicit password handoff remains intentionally unsupported while the v1 PsExec boundary forbids plaintext `psexec -u/-p` password passing.

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
dispatch doctor [--output rich|json|table]
```

Status: partial/current diagnostics, with expanded transport-aware checks planned v1.

`doctor` reports local readiness. It does not remediate endpoints.

Current diagnostics are local prerequisite checks only. Expanded `--transport psexec|psrp|winrm|auto` behavior is planned but not accepted by the current command, so operators cannot request transport-specific prerequisite checks yet.

Use `doctor` when validating the admin workstation or explaining why Dispatch cannot start a local workflow. Use future `hosts test` behavior for endpoint-specific transport checks; `doctor` is not a fleet scan.

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

Dispatch returns success only when command validation succeeds and the selected targets complete according to the expected exit-code policy. Transport, authentication, authorization, probe, timeout, and script failures are mapped to stable result fields and failure categories; automation should read `Admin\results.json` for per-target detail.
