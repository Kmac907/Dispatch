# Job Schema

This is the v1 YAML job contract for `dispatch apply`.

Status: partial/current. `dispatch apply <job.yml> --plan`, `dispatch apply <job.yml> --check`, and `dispatch apply <job.yml>` currently support selected multi-task script-first `ps`, scalar `cmd`, and scalar `exe` jobs in YAML order. `copy` tasks are currently plan/check-only.

## Top-Level Fields

```yaml
name: Fix endpoints
description: Optional description
hosts: kiosks
transport: psrp
credential: prod-admin
strategy:
  serial: 10
defaults:
  expected_exit_codes: [0, 3010]
vars:
  package: example
tasks:
  - ps: .\Fix.ps1
    tags: [prod, fix]
  - copy:
      src: .\payloads\agent.msi
      dest: C:\ProgramData\Dispatch\Payloads\agent.msi
      overwrite: true
      tags: [stage]
```

Supported top-level fields:

- `name`
- `description`
- `hosts`
- `transport`
- `credential`
- `strategy`
- `defaults`
- `vars`
- `tasks`

## Precedence

Target selection:

1. Explicit CLI `--target`.
2. `job.hosts`.
3. Explicit CLI `--exclude` filters the selected target set.

Inventory:

1. Explicit CLI `--inventory`.
2. Explicit `--config`.
3. Ambient global config.
4. No inventory.

Transport:

1. Explicit CLI `--transport` when not `auto`.
2. `job.transport` when not `auto`.
3. Inventory transport policy.
4. Explicit `--config`.
5. Ambient global config.
6. Built-in default.

Credential:

1. CLI `--credential`.
2. `job.credential`.
3. Host credential.
4. Group credential.
5. Inventory defaults credential.
6. No credential.

## Validation

Validation must fail before endpoint work for:

- invalid YAML
- missing required fields
- unsupported task type
- unsupported field
- unsupported vars-source concepts such as `group_vars`, `host_vars`, `vars_files`, or `include_vars`
- `transport` under `job.vars`
- plaintext secret-like keys
- unresolved hosts or inventory selectors
- unsupported transport/payload combinations

## Current Apply Subset

The current `apply` slice supports plan and check for:

- selected `tasks` entries using `ps`, `cmd`, or `exe`
- selected `copy` tasks for `--plan` and `--check` only
- `hosts`
- `transport`
- `credential`
- `defaults.expected_exit_codes`
- `strategy.serial`
- scalar `vars` values passed to `ps` tasks as named PowerShell script arguments
- task-level `tags` on supported `ps`, `cmd`, `exe`, and plan/check `copy` tasks

Relative `ps` and `copy.src` task paths resolve from the job file directory, scalar `cmd` tasks run through the same command payload path as `dispatch run cmd`, and scalar `exe` tasks run through the same command payload path as `dispatch run exe`. `copy.dest` must be a rooted Windows path, and `copy.overwrite` defaults to `false` when omitted. `--plan` and `--check` can render plans for multiple selected `ps`, `cmd`, and `exe` tasks plus selected `copy` task previews without endpoint work; `--check` does not simulate task side effects. Execution runs selected `ps`, `cmd`, and `exe` tasks in YAML order, stops after the first failed task run, and reuses the same planner, credential resolution, executor, live-rendering, and result-output path as `dispatch run`; selected `copy` execution is rejected before endpoint planning until file transfer execution exists. Scalar `job.vars` entries become named script arguments for each selected `ps` task in YAML order, for example `message: fixed` becomes `-message fixed`; variable names must use letters, numbers, or underscores, and start with a letter or underscore. Inventory vars remain host/group metadata and are not merged into runtime task inputs. `--tags <tags>` and `--skip-tags <tags>` filter supported tasks by comma-separated task tags before planning or endpoint work. `--serial <n>` overrides `strategy.serial` for the supported apply subset. Apply transport resolution honors explicit non-`auto` CLI transport, non-`auto` job transport, inventory transport policy, and then config/default transport; mixed selected inventory transport policies fail before planning unless the operator supplies an explicit concrete transport. `--no-progress`, `--quiet`, `--verbose`, and `--trace` follow the same current output/log behavior as `dispatch run`; `--diff` is recognized but fails before planning until the diff behavior slice is implemented. Other planned task types remain later `6.5` work.

`job.vars` is only an inline runtime/task-input bag. Transport selection must use the top-level `transport` field, and separate vars-file concepts are rejected before endpoint work.

## Secret Boundary

Job files may contain endpoint credential reference names, but must not contain passwords, tokens, SAS values, or secret material.

`credential` is not a script secret. The `dispatch run ps ... --secret name=reference` surface is separate from the current job schema. If job-level script secrets are added later, they must follow the same configured-reference and script-parameter binding model, plan output must render only redacted script-parameter bindings, and real execution must not write secret values to command lines, logs, results, traces, or artifacts.
