# Jobs

`dispatch apply <job.yml>` is the declared-job surface for v1.

Status: partial/current. `dispatch apply <job.yml> --plan` and `dispatch apply <job.yml> --check` support selected multi-task script-first `ps` plans. `dispatch apply <job.yml>` execution currently runs exactly one selected `ps` task. Multi-task execution and additional task types remain planned v1.

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

The current implementation converts the supported job subset into the same planner, credential resolution, executor, live-rendering, and result-output path used by `dispatch run ps`.

`--check` validates the supported job subset and renders the resolved plan without endpoint work. It does not simulate PowerShell script side effects.

`--serial <n>` or `--concurrency <n>` overrides `strategy.serial` for the supported apply subset. They are aliases for the same batch-size control and cannot be used together.

`--target <selector>` overrides the job `hosts` selector for the current run. `--inventory <path>` overrides the configured inventory path. `--exclude <selector>` filters the selected targets after the job or CLI target selector is resolved.

Transport selection follows the apply precedence contract: explicit CLI `--transport` values other than `auto` win, `--transport auto` falls through to non-`auto` `job.transport`, then inventory transport policy, then config/default transport. If selected inventory hosts resolve to conflicting transport policies and no explicit concrete transport is supplied, validation fails before planning.

Task tags are optional on `ps` tasks. `--tags <tags>` selects tasks when at least one tag matches, and `--skip-tags <tags>` excludes tasks when any tag matches. If filters remove every supported task, validation fails before endpoint work.

`--no-progress` disables live progress for apply execution, `--quiet` suppresses rich non-error output, and `--verbose` / `--trace` control NDJSON diagnostic detail. `--diff` is recognized but fails before planning until the diff behavior slice is implemented.

## Job Credential

`credential` selects a reference from the loaded global Dispatch config. The job file does not define provider details or passwords.

## Job Variables

`job.vars` is the v1 runtime/task-input variable bag. Inventory vars are host/group metadata and are not automatically merged into runtime task variables.

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

The current implementation accepts multiple `ps` tasks for `--plan` and `--check`. Execution still requires the selected task set to resolve to exactly one `ps` task. Unsupported task types fail validation before endpoint work.
