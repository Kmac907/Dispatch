# Command Reference

`dispatch.exe` is the canonical command surface. The planned PowerShell module will wrap this executable and will not provide a separate execution engine.

Status legend:

- Current: implemented in the current codebase.
- Planned v1: required by the v1 roadmap but not fully implemented yet.
- Deferred: explicitly outside v1.

## Root

```powershell
dispatch --help
dispatch version
```

Status: current.

- `dispatch --help` prints the registered command tree and high-level examples.
- `dispatch version` prints the CLI version/build identity available to the executable.

## Apply

```powershell
dispatch apply <job.yml> [--inventory <hosts.yml>] [--target <selector>] [--exclude <selector>] [--config <path>] [--credential <name>] [--transport auto|psrp|winrm|psexec] [--tags <tags>] [--skip-tags <tags>] [--serial <n>|--concurrency <n>] [--plan|--check] [--diff] [--output rich|table|json|ndjson|yaml] [--no-color] [--no-progress] [--quiet] [-v|--verbose] [--trace]
```

Status: partial/current. Plan, check, and execution are implemented for selected multi-task script-first `ps`, scalar `cmd`, and scalar `exe` jobs in YAML order. Plan/check-only rendering is implemented for selected `copy` tasks. Additional task types remain planned v1.

Runs a declared YAML job. The v1 job model is script-first and converts supported tasks into the same planning/execution contracts used by ad-hoc commands.

Scalar `job.vars` entries are passed to selected `ps` tasks as named PowerShell script arguments in YAML order. Scalar `cmd` tasks run through the same command payload path as `dispatch run cmd`, and scalar `exe` tasks run through the same command payload path as `dispatch run exe`. Selected `copy` tasks are plan/check-only and use mapping syntax such as `copy: { src: .\payloads\agent.msi, dest: C:\ProgramData\Dispatch\Payloads\agent.msi, overwrite: true }` or a block with `src`, `dest`, `overwrite`, and `tags` fields. Inventory vars remain host/group metadata and do not become runtime task inputs.

Use `--target <selector>` to override `hosts` from the job file for the current run. Use `--inventory <path>` to override the configured inventory path. Use `--exclude <selector>` to remove hosts from the selected set after the job or CLI target selector is resolved.

Use `--transport <value>` to override job, inventory, and config transport policy. `--transport auto` is a fall-through value: Dispatch uses non-`auto` `job.transport`, then inventory transport policy, then config/default transport. If the selected inventory hosts resolve to conflicting transport policies and no explicit non-`auto` transport is supplied, validation fails before planning.

Use `--tags <tags>` to select `ps`, `cmd`, `exe`, and plan/check `copy` tasks when at least one task tag matches. Use `--skip-tags <tags>` to exclude supported tasks when any task tag matches. Tag values are comma-separated, and a filter that excludes every supported task fails before endpoint work. Execution runs the selected `ps`, `cmd`, and `exe` tasks in YAML order and stops after the first failed task run. Real `copy` execution is rejected before endpoint planning until file transfer execution is implemented.

Use `--plan` to inspect the resolved plan. Use `--check` to validate and render the supported job subset without endpoint work. The current check mode does not simulate task side effects.

Use `--serial <n>` or `--concurrency <n>` to override `strategy.serial` for the supported apply subset. These two options control the same batch-size setting and cannot be used together.

Use `--no-progress` to disable live widgets, `--quiet` to suppress rich non-error output, and `--verbose` / `--trace` for more detailed NDJSON diagnostics. `--diff` is accepted as an explicit planned setting but fails before planning until the diff behavior slice is implemented.

## Run

```powershell
dispatch run ps <script.ps1> [args] --target <selector> [--inventory <hosts.yml>] [--transport psrp|winrm|psexec] [--credential <name>]
dispatch run cmd <command> --target <selector> [--transport psrp|winrm]
dispatch run exe <path> --target <selector> [--transport psrp|winrm]
```

Status: `run ps`, `run cmd`, and `run exe` route through the shared planner/executor when the transport supports the payload. Compatibility routing for the older `dispatch run --script ...` shape remains for migration.

Common options:

- `--target`
- `--target-file`
- `--inventory`
- `--config`
- `--transport`
- `--credential`
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

Subcommands:

- `run ps <script.ps1>` runs a PowerShell script file. Extra arguments after the script path are passed through as script arguments. Use this for script-first automation and installer/remediation scripts.
- `run cmd <command>` runs a shell command through the selected command-capable transport. Use this for short Windows command-line checks such as `whoami`.
- `run exe <path>` runs an executable path through the selected command-capable transport. Use this for direct process execution when a script wrapper is not needed.

The compatibility shape `dispatch run --script <path> --computer-name <names>` still works for older callers, but new docs and automation should use `dispatch run ps`.

## Push

```powershell
dispatch push <source> --dest <remote-path> [--inventory <path>] [--target <selector>] [--exclude <selector>] [--transport auto|winrm|psrp] [--credential <name>] [--config <path>] [--concurrency <n>] [--overwrite] [--plan|--check] [--recurse] [--checksum] [--backup] [--execute] [--cleanup] [--output rich|table|json|ndjson|yaml] [--no-color] [--no-progress] [--quiet] [-v|--verbose] [--trace]
```

Status: partial current.

Current support: single-file push and recursive directory push over raw WinRM or PSRP, target selection, inventory selection, `--transport auto` selection through inventory/config/default transport policy, optional replacement through `--overwrite`, target-local pre-replacement backup through `--backup`, explicit push-result SHA-256 enforcement/reporting through `--checksum`, single-file `.ps1` execute-after-copy through `--execute`, and structured output. `--plan` and `--check` preview the transfer without writing. Push cleanup and PsExec push remain planned or deferred.

Options and behavior:

- `<source>` is the local file or directory to transfer. Directory transfer requires `--recurse` and preserves relative file paths under `--dest`.
- `--dest <remote-path>` is the drive-qualified Windows path to write on each target, such as `C:\ProgramData\Dispatch\Payloads\agent.msi`.
- `--overwrite` allows replacing an existing remote file. Without it, Dispatch creates the remote file only if it does not already exist; an existing remote file fails that target instead of being replaced. For directory push, this applies independently to each file under `--dest`.
- `--backup` requires `--overwrite`. When the destination file already exists, Dispatch creates a target-local copy beside the destination before replacing it and reports the backup path in result metadata. If the destination file does not exist, the push succeeds without creating a backup.
- `--checksum` requires the selected transfer transport to include SHA-256 metadata in the push result and fails the target if the reported remote hash does not match the local source hash. The current raw WinRM and PSRP upload clients already compare the remote SHA-256 before reporting upload success; this flag makes that verification explicit in push output and failure policy.
- `--execute` runs the uploaded file after a successful upload when the source is a single `.ps1` file. The current implementation invokes `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <remote-path>` through the selected WinRM or PSRP transport, records execution metadata in the push result, and fails the target if execution fails or exits with a nonzero code. Directory execution, non-PowerShell payload execution, argument passing, and cleanup remain later push work.
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

Planned subcommands:

- `hosts list` will read an inventory and print the hosts Dispatch can resolve, including group membership where available.
- `hosts test` will test the selected remoting path for matching hosts. It is not an ICMP ping command and will not remediate endpoints.
- `hosts validate` will validate inventory syntax/schema without running endpoint work.
- `hosts graph` will show group/child relationships so nested inventory structure is easier to inspect.
- `hosts vars` will show the effective host metadata Dispatch can derive for one target, including supported transport and credential reference fields.

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

Subcommands:

- `logs list` enumerates local runs under the configured run root and shows reduced run summaries.
- `logs show <run-id|latest>` prints a selected run summary, target outcomes, and output file locations.
- `logs tail <run-id|latest> --count <n>` reads the durable NDJSON event stream and prints the latest events.
- `logs export <run-id|latest> --dest <path>` copies the selected run's durable summary/event data to an export folder and generates a CSV summary where available.
- `logs retry <run-id|latest>` builds a read-only retry plan for failed, timed-out, or cancelled targets. It does not rerun anything.

## Credentials

```powershell
dispatch creds list
dispatch creds add <name> [--force]
dispatch creds test <name>
dispatch creds remove <name>
```

Status: current for config-defined prompt, DPAPI file, Windows Credential Manager, and Azure Key Vault provider behavior on PSRP and raw WinRM credential resolution. PowerShell-wrapper `PSCredential` handoff remains planned for the wrapper slice. PsExec explicit password handoff remains intentionally unsupported while the v1 PsExec boundary forbids plaintext `psexec -u/-p` password passing.

Credential names are references from the loaded global YAML config. Resolved passwords are never command-line arguments.

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

## Init

```powershell
dispatch init config
dispatch init hosts
dispatch init job
dispatch init all
```

Status: current.

Scaffolds starter YAML files in the current directory: `config.yml`, `hosts.yml`, and `job.yml`. `init all` creates all three files. Init refuses to overwrite an existing starter file.

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
