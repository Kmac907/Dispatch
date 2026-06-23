# Job Schema

This is the v1 YAML job contract for `dispatch apply`.

Status: partial/current. `dispatch apply <job.yml> --plan` and `dispatch apply <job.yml> --check` currently support selected multi-task script-first `ps` plans. `dispatch apply <job.yml>` execution currently runs exactly one selected `ps` task.

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

- selected `tasks` entries using `ps`
- `hosts`
- `transport`
- `credential`
- `defaults.expected_exit_codes`
- `strategy.serial`
- task-level `tags` on supported `ps` tasks

Relative `ps` task paths resolve from the job file directory. `--plan` and `--check` can render plans for multiple selected `ps` tasks without endpoint work; `--check` does not simulate script side effects. Execution reuses the same planner, credential resolution, executor, live-rendering, and result-output path as `dispatch run ps`, and currently requires tag filters to leave exactly one selected `ps` task. `--tags <tags>` and `--skip-tags <tags>` filter supported tasks by comma-separated task tags before planning or endpoint work. `--serial <n>` or `--concurrency <n>` overrides `strategy.serial` for the supported apply subset. Apply transport resolution honors explicit non-`auto` CLI transport, non-`auto` job transport, inventory transport policy, and then config/default transport; mixed selected inventory transport policies fail before planning unless the operator supplies an explicit concrete transport. `--no-progress`, `--quiet`, `--verbose`, and `--trace` follow the same current output/log behavior as `dispatch run ps`; `--diff` is recognized but fails before planning until the diff behavior slice is implemented. Multi-task execution and other planned task types remain later `6.5` work.

`job.vars` is only an inline runtime/task-input bag. Transport selection must use the top-level `transport` field, and separate vars-file concepts are rejected before endpoint work.

## Secret Boundary

Job files may contain credential reference names, but must not contain passwords, tokens, SAS values, or secret material.
