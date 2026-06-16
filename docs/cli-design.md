# Dispatch CLI Design

Status: draft, partially implemented. Current implementation registers the documented command tree through Spectre.Console.Cli, preserves `dispatch run --script ...` through a compatibility parser, renders real execution through a Spectre `LiveDisplay`, supports initial structured output modes, current-path output-control flags, current-path NDJSON stdout event streaming, initial inventory/target selectors for `run ps`, and initial inventory transport precedence from YAML defaults/group vars/host vars, and keeps YAML jobs, durable run logs, credentials, push/hosts/init behavior, `run cmd`, and `run exe` execution as roadmap work. Transport execution is still PsExec-only today; raw WinRM and PSRP are the next transport roadmap slices because the current validation environment has working WinRM targets and unreliable `\\<device>\C$` admin-share staging for PsExec-first validation.

This document records the active CLI design that supersedes the earlier Terminal.Gui command-center direction. `docs/plan.md` remains the roadmap source of truth; this file gives the command and output contract in one place.

## Product Shape

Dispatch is a Windows-native C# automation runner for Windows hosts. The product design covers ad-hoc execution, declared jobs, host inventories, structured logs, credential references, and explicit transports. Current implementation status is tracked in `docs/plan.md` and `workflow/build/implementation-plan.md`.

Current transport priority is raw WinRM first, then PSRP. PsExec remains an explicit transport in the command contract and the only implemented executor today, but additional PsExec-first roadmap work is deferred until the target environment provides reliable admin-share staging.

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

Accepted transport names are broader than the currently implemented executors. At the moment, `psrp` and `winrm` are command-contract values and roadmap targets, not working execution paths.

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

Initial inventory direction is also explicit rather than arbitrary YAML. The current `run ps` implementation supports a small YAML inventory subset for `defaults`, `groups`, `hosts`, host `tags`, and inventory transport precedence from YAML defaults/group vars/host vars. Unsupported inventory sections and unsupported fields inside the current subset fail validation clearly. Broader inventory schema behavior, additional host/group vars, credential references, and job/config precedence remain roadmap work.

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
