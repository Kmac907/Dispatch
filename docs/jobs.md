# Jobs

`dispatch apply <job.yml>` is the declared-job surface for v1.

Status: partial/current. `dispatch apply <job.yml>`, `dispatch apply <job.yml> --plan`, and `dispatch apply <job.yml> --check` are implemented for one script-first `ps` task. Multi-task jobs and additional task types remain planned v1.

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
```

Run:

```powershell
dispatch apply .\job.yml --plan --output json
dispatch apply .\job.yml --check --output json
dispatch apply .\job.yml --serial 10
dispatch apply .\job.yml --plan --credential breakglass-admin
dispatch apply .\job.yml --credential breakglass-admin
```

The current implementation converts the supported job subset into the same planner, credential resolution, executor, live-rendering, and result-output path used by `dispatch run ps`.

`--check` validates the supported job subset and renders the resolved plan without endpoint work. It does not simulate PowerShell script side effects.

`--serial <n>` or `--concurrency <n>` overrides `strategy.serial` for the supported apply subset. They are aliases for the same batch-size control and cannot be used together.

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

The current implementation accepts exactly one `ps` task for plan and execution. Unsupported task types and multiple tasks fail validation before endpoint work.
