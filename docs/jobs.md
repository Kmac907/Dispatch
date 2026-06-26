# Jobs

`dispatch apply <job.yml>` is the declared-job surface for v1.

Status: partial/current. `dispatch apply <job.yml> --plan`, `dispatch apply <job.yml> --check`, and `dispatch apply <job.yml>` support selected multi-task script-first `ps`, scalar `cmd`, and scalar `exe` jobs in YAML order. `copy` tasks are supported for plan/check rendering only. Additional task types remain planned v1.

## Purpose

Use a job file when the work should be repeatable and checked into source control.

## Example

```yaml
name: Fix endpoints
description: Run endpoint fix script
hosts: kiosks
transport: psrp
credential: prod-admin

vars:
  message: fixed

tasks:
  - ps: .\Fix.ps1
    tags: [prod, fix]
  - cmd: whoami
    tags: [audit]
  - exe: .\tools\repair.exe /quiet
    tags: [install]
  - copy:
      src: .\payloads\agent.msi
      dest: C:\ProgramData\Dispatch\Payloads\agent.msi
      overwrite: true
      tags: [stage]
```

Run:

```powershell
dispatch apply .\job.yml --plan --output json
dispatch apply .\job.yml --check --output json
dispatch apply .\job.yml --serial 10
dispatch apply .\job.yml --inventory .\hosts.yml --target kiosks --exclude KIOSK03 --plan
dispatch apply .\job.yml --tags prod --skip-tags staging --plan
dispatch apply .\job.yml --output ndjson --trace
dispatch apply .\job.yml --plan --quiet
dispatch apply .\job.yml --plan --credential breakglass-admin
dispatch apply .\job.yml --credential breakglass-admin
```

The current implementation converts executable `ps`, `cmd`, and `exe` tasks into the same planner, credential resolution, executor, live-rendering, and result-output path used by `dispatch run`. `copy` tasks are preview records only for `--plan` and `--check`.

`--check` validates the supported job subset and renders the resolved plan without endpoint work. It does not simulate PowerShell script side effects.

`--serial <n>` overrides `strategy.serial` for the supported apply subset. It controls how many selected targets Dispatch includes in each apply batch.

`--target <selector>` overrides the job `hosts` selector for the current run. `--inventory <path>` overrides the configured inventory path. `--exclude <selector>` filters the selected targets after the job or CLI target selector is resolved.

Transport selection follows the apply precedence contract: explicit CLI `--transport` values other than `auto` win, `--transport auto` falls through to non-`auto` `job.transport`, then inventory transport policy, then config/default transport. If selected inventory hosts resolve to conflicting transport policies and no explicit concrete transport is supplied, validation fails before planning.

Task tags are optional on `ps`, `cmd`, `exe`, and plan/check `copy` tasks. `--tags <tags>` selects tasks when at least one tag matches, and `--skip-tags <tags>` excludes tasks when any tag matches. If filters remove every supported task, validation fails before endpoint work.

`copy` supports block or inline mapping syntax with required `src` and `dest` fields plus optional `overwrite`, which defaults to `false`. Relative `src` paths resolve from the job file directory and must exist for selected `--plan` / `--check` tasks. `dest` must be a rooted Windows path. Normal `dispatch apply` rejects selected `copy` tasks before endpoint planning until file transfer execution is implemented.

`--no-progress` disables live progress for apply execution, `--quiet` suppresses rich non-error output, and `--verbose` / `--trace` control NDJSON diagnostic detail. `--diff` is recognized but fails before planning until the diff behavior slice is implemented.

## Job Credential

`credential` selects a reference from the loaded global Dispatch config. The job file does not define provider details or passwords.

`credential` is only the endpoint authentication reference. It does not pass SAS tokens, API keys, or other script inputs to tasks.

## Script Secrets

Script secret handoff currently exists only as the `dispatch run ps ... --secret name=reference` plan/dry-run surface. It is not part of the current `apply` subset. When job-level script secrets are added, they must follow the same redacted script-parameter model and no-secret-values-in-logs rule.

## Job Variables

`job.vars` is the v1 runtime/task-input variable bag. Current `ps` apply tasks pass scalar `job.vars` entries to scripts as named PowerShell arguments in YAML order, for example `message: fixed` becomes `-message fixed`. Variable names must use letters, numbers, or underscores, and start with a letter or underscore. Inventory vars are host/group metadata and are not automatically merged into runtime task variables.

Transport is not allowed under `job.vars`; use the top-level `transport` field. Separate vars-file concepts such as `group_vars`, `host_vars`, `vars_files`, and `include_vars` are not part of v1 jobs and fail validation before endpoint work.

## Task Vocabulary

Planned v1 task vocabulary:

- `ps`
- `cmd`
- `exe`
- `copy`
- `fetch`
- `wait`
- `reboot`

The current implementation accepts multiple selected `ps`, `cmd`, and `exe` tasks for `--plan`, `--check`, and execution, plus selected `copy` tasks for `--plan` and `--check` only. Unsupported task types fail validation before endpoint work.
