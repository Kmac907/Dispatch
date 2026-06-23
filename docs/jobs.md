# Jobs

`dispatch apply <job.yml>` is the declared-job surface for v1.

Status: partial/current. `dispatch apply <job.yml> --plan` is implemented for one script-first `ps` task. Full job execution and additional task types remain planned v1.

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
dispatch apply .\job.yml --plan --credential breakglass-admin
dispatch apply .\job.yml --credential breakglass-admin
```

The current implementation plans jobs only. `dispatch apply .\job.yml` without `--plan` remains a planned execution command until the next job execution slice lands.

## Job Credential

`credential` selects a reference from the loaded global Dispatch config. The job file does not define provider details or passwords.

## Job Variables

`job.vars` is the v1 runtime/task-input variable bag. Inventory vars are host/group metadata and are not automatically merged into runtime task variables.

## Task Vocabulary

Planned v1 task vocabulary:

- `ps`
- `cmd`
- `exe`
- `copy`
- `fetch`
- `wait`
- `reboot`

The current implementation accepts exactly one `ps` task for `--plan`. Unsupported task types and multiple tasks fail validation before endpoint work.
