# Dispatch CLI Design

Status: draft, partially implemented. Current implementation registers the documented command tree through Spectre.Console.Cli, preserves `dispatch run --script ...` through a compatibility parser, renders real execution through a Spectre `LiveDisplay`, supports initial structured output modes, current-path output-control flags, current-path NDJSON stdout event streaming, initial inventory/target selectors for `run ps`, initial inventory transport precedence from YAML defaults/group vars/host vars including inline transport maps in the current supported subset, and a narrow raw WinRM slice that wires `winrm` into DI, validates `winrm + ScriptPayload`, plans endpoint-local script paths, probes DNS plus default WinRM TCP ports, and returns an explicit not-yet-implemented execution failure after probe. YAML jobs, durable run logs, credentials, push/hosts/init behavior, `run cmd`, and `run exe` execution remain roadmap work.

This document records the active CLI design that supersedes the earlier Terminal.Gui command-center direction. `docs/plan.md` remains the roadmap source of truth; this file gives the command and output contract in one place.

## Product Shape

Dispatch is a Windows-native C# automation runner for Windows hosts. The product design covers ad-hoc execution, declared jobs, host inventories, structured logs, credential references, and explicit transports. Current implementation status is tracked in `docs/plan.md` and `workflow/build/implementation-plan.md`.

Current transport priority is raw WinRM first, then PSRP. PsExec remains the only end-to-end execution transport today. The current raw WinRM slice is limited to request validation, planning, DI registration, and endpoint reachability probes; execution, transfer, and artifact collection still return explicit not-yet-implemented behavior.

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

Accepted transport names are broader than the currently implemented executors. `psrp` remains a command-contract value and roadmap target. `winrm` is now a partial execution path that can validate requests, build plans, and probe endpoint reachability, but it still returns an explicit not-yet-implemented failure for raw WinRM script execution.

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

Precedence:

```text
CLI flag > job YAML > inventory host/group vars > user config > machine config > defaults
```

This is the long-term product precedence, not the current `6.2` completion boundary by itself. For the current non-job `run ps` path, `6.2` only owns:

- CLI defaults/overrides for `inventory`, `target`, `exclude`, and `transport`
- inventory transport policy from the supported YAML subset
- explicit `--config` defaults
- ambient bound `Dispatch` config defaults
- application defaults

Job YAML precedence belongs to roadmap item `6.5`. Credential-reference precedence belongs to roadmap item `6.4`.

## Output Model

Dispatch uses Spectre.Console for human terminal output and durable JSON/NDJSON/YAML files or streams for automation.

The terminal has four phases:

1. Parse and validate: stable text and tables.
2. Preflight: `Status` for indeterminate work and `Progress` for measurable work.
3. Execution: one `LiveDisplay` dashboard driven by internal run events.
4. Final summary: stable post-live result summary with run ID, counts, and log paths.

Workers must not write directly to `AnsiConsole`. They emit events. A single renderer consumes events and owns live terminal output.

Current execution rendering uses `DispatchExecutionProgress` events, a channel-fed renderer, and one Spectre `LiveDisplay` for live runs. Planning uses Spectre `Status` or `Progress` only during active work. Redirected sessions and `--no-progress` use stable non-live rendering.

## Structured Output

`--output json` emits one valid JSON document and suppresses decorative UI.

`--output ndjson` emits one stdout event per line and suppresses decorative UI for the current `run ps` path. Durable `events.ndjson` run-history files belong to the later log-command work.

`--no-progress` disables live widgets and keeps terminal output stable for CI and redirected hosts.

## Inventory And Jobs

YAML inventory is the documented default because it supports groups, variables, transport policy, and credential references. Simple text host files remain supported for quick ad-hoc work.

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

Initial task vocabulary is also explicit and closed. Unsupported fields, unsupported task types, unsafe secret fields, and unsupported selector expressions must fail validation before any endpoint probe, staging, or execution begins.

Initial inventory direction is also explicit rather than arbitrary YAML. The `6.2` completion boundary for the current `run ps` path is a closed subset:

- top-level sections: `defaults`, `groups`, `hosts`
- supported fields only:
  - `defaults.transport`
  - `groups.<group>.hosts`
  - `groups.<group>.children`
  - `groups.<group>.vars.transport`
  - `hosts.<host>.tags`
  - `hosts.<host>.vars.transport`
- supported syntax today:
  - top-level `hosts:` block-list, inline-list, mapping-form, and inline-map host entries
  - `groups.<group>.hosts` / `children` block-list, inline-list, and mapping-form entries
  - block-map or inline-map syntax for the supported `transport` fields, including `defaults: { transport: winrm }`, `groups.<group>.vars: { transport: psrp }`, and `hosts.<host>.vars: { transport: psexec }`
  - block-list or inline-list tags

Explicit `--config` overrides ambient config values where it supplies them, and inventory transport still overrides config/default transport when CLI transport is omitted. Defaults-only inventories are treated as YAML and fail clearly when no real hosts are selected, rather than being parsed as text host files. Unsupported inventory sections and unsupported fields inside the current subset fail validation clearly.

This item does not own generic host/group variable bags, credential references, or job YAML merge behavior. Those belong to later roadmap items. `6.2` is now complete unless the roadmap is explicitly amended.

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
