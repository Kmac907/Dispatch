# Job Schema

This is the v1 YAML job contract for `dispatch apply`.

Status: partial/current. `dispatch apply <job.yml>`, `dispatch apply <job.yml> --plan`, and `dispatch apply <job.yml> --check` currently support a script-first subset with one `ps` task.

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
2. `job.transport`.
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

The current `apply` slice supports plan and execution for:

- one `tasks` entry using `ps`
- `hosts`
- `transport`
- `credential`
- `defaults.expected_exit_codes`
- `strategy.serial`

Relative `ps` task paths resolve from the job file directory. Execution reuses the same planner, credential resolution, executor, live-rendering, and result-output path as `dispatch run ps`. `--check` validates the supported job subset and renders the resolved plan without endpoint work; it does not simulate script side effects. `--serial <n>` or `--concurrency <n>` overrides `strategy.serial` for the supported apply subset. Multiple tasks and other planned task types remain later `6.5` work.

`job.vars` is only an inline runtime/task-input bag. Transport selection must use the top-level `transport` field, and separate vars-file concepts are rejected before endpoint work.

## Secret Boundary

Job files may contain credential reference names, but must not contain passwords, tokens, SAS values, or secret material.
