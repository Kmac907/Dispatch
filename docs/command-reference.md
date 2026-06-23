# Command Reference

`dispatch.exe` is the canonical command surface. The PowerShell module wraps this executable and does not provide a separate execution engine.

Status legend:

- Current: implemented in the current codebase.
- Planned v1: required by the v1 roadmap but not fully implemented yet.
- Deferred: explicitly outside v1.

## Root

```powershell
dispatch --help
dispatch version
```

`version` is current.

## Apply

```powershell
dispatch apply <job.yml> [--inventory <hosts.yml>] [--target <selector>] [--exclude <selector>] [--config <path>] [--credential <name>] [--transport auto|psrp|winrm|psexec] [--tags <tags>] [--skip-tags <tags>] [--serial <n>|--concurrency <n>] [--plan|--check] [--diff] [--output rich|table|json|ndjson|yaml] [--no-color] [--no-progress] [--quiet] [-v|--verbose] [--trace]
```

Status: partial/current. Plan and check are implemented for selected multi-task script-first `ps` jobs. Execution currently runs exactly one selected `ps` task. Multi-task execution and additional task types remain planned v1.

Runs a declared YAML job. The v1 job model is script-first and converts supported tasks into the same planning/execution contracts used by ad-hoc commands.

Use `--target <selector>` to override `hosts` from the job file for the current run. Use `--inventory <path>` to override the configured inventory path. Use `--exclude <selector>` to remove hosts from the selected set after the job or CLI target selector is resolved.

Use `--transport <value>` to override job, inventory, and config transport policy. `--transport auto` is a fall-through value: Dispatch uses non-`auto` `job.transport`, then inventory transport policy, then config/default transport. If the selected inventory hosts resolve to conflicting transport policies and no explicit non-`auto` transport is supplied, validation fails before planning.

Use `--tags <tags>` to select `ps` tasks when at least one task tag matches. Use `--skip-tags <tags>` to exclude `ps` tasks when any task tag matches. Tag values are comma-separated, and a filter that excludes every supported task fails before endpoint work. Execution requires the selected task set to contain exactly one `ps` task.

Use `--plan` to inspect the resolved plan. Use `--check` to validate and render the supported job subset without endpoint work. The current check mode does not simulate script side effects.

Use `--serial <n>` or `--concurrency <n>` to override `strategy.serial` for the supported apply subset. These two options control the same batch-size setting and cannot be used together.

Use `--no-progress` to disable live widgets, `--quiet` to suppress rich non-error output, and `--verbose` / `--trace` for more detailed NDJSON diagnostics. `--diff` is accepted as an explicit planned setting but fails before planning until the diff behavior slice is implemented.

## Run

```powershell
dispatch run ps <script.ps1> [args] --target <selector> [--inventory <hosts.yml>] [--transport psrp|winrm|psexec] [--credential <name>]
dispatch run cmd <command> --target <selector> [--transport psrp|winrm]
dispatch run exe <path> --target <selector> [--transport psrp|winrm|psexec]
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

## Push

```powershell
dispatch push <source> --dest <remote-path> [--target <selector>] [--recurse] [--checksum] [--overwrite] [--backup] [--execute] [--cleanup]
```

Status: planned v1.

Copies files to endpoints and can optionally execute after transfer.

## Hosts

```powershell
dispatch hosts list --inventory <hosts.yml>
dispatch hosts test --inventory <hosts.yml> --target <selector> [--transport psrp|winrm|psexec|auto]
dispatch hosts validate --inventory <hosts.yml>
dispatch hosts graph --inventory <hosts.yml>
dispatch hosts vars --inventory <hosts.yml> --target <host>
```

Status: planned v1 command group. Inventory parsing and selector behavior are partially implemented on current run paths.

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

## Credentials

```powershell
dispatch creds list
dispatch creds add <name> [--force]
dispatch creds test <name>
dispatch creds remove <name>
```

Status: current for config-defined prompt, DPAPI file, Windows Credential Manager, and Azure Key Vault provider behavior on PSRP and raw WinRM credential resolution. PowerShell-wrapper `PSCredential` handoff remains planned for the wrapper slice. PsExec explicit password handoff remains intentionally unsupported while the v1 PsExec boundary forbids plaintext `psexec -u/-p` password passing.

Credential names are references from the loaded global YAML config. Resolved passwords are never command-line arguments.

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
dispatch doctor [--transport psexec|psrp|winrm|auto] [--output rich|json|table]
```

Status: partial/current diagnostics, with expanded transport-aware checks planned v1.

`doctor` reports local readiness. It does not remediate endpoints.

## Init

```powershell
dispatch init config
dispatch init hosts
dispatch init job
dispatch init all
```

Status: planned v1.

Scaffolds starter YAML files.

## Output Modes

- `rich` - Spectre.Console operator UI.
- `table` - concise human-readable text/table output.
- `json` - stable automation document.
- `ndjson` - one event per line where supported.
- `yaml` - stable YAML document where supported.

Automation should prefer result files or structured output modes instead of parsing rich terminal output.

## Exit Behavior

Dispatch returns success only when command validation succeeds and the selected targets complete according to the expected exit-code policy. Transport, authentication, authorization, probe, timeout, and script failures are mapped to stable result fields and failure categories; automation should read `Admin\results.json` for per-target detail.
