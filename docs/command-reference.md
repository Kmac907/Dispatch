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
dispatch apply <job.yml> [--config <path>] [--credential <name>] [--plan] [--check] [--output rich|table|json|ndjson|yaml]
```

Status: planned v1.

Runs a declared YAML job. The v1 job model is script-first and converts supported tasks into the same planning/execution contracts used by ad-hoc commands.

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

Status: current for config-defined prompt, DPAPI file, Windows Credential Manager, and Azure Key Vault provider behavior on PSRP credential resolution. PowerShell-wrapper `PSCredential` handoff and non-PSRP credential handoff are planned v1.

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
