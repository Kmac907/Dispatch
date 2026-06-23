# Job Schema

This is the v1 YAML job contract for `dispatch apply`.

Status: partial/current. `dispatch apply <job.yml> --plan` currently supports a script-first subset with one `ps` task.

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
- plaintext secret-like keys
- unresolved hosts or inventory selectors
- unsupported transport/payload combinations

## Current Apply Plan Subset

The current `apply --plan` slice supports:

- one `tasks` entry using `ps`
- `hosts`
- `transport`
- `credential`
- `defaults.expected_exit_codes`
- `strategy.serial`

Relative `ps` task paths resolve from the job file directory. Full job execution, multiple tasks, and other planned task types remain later `6.5` work.

## Secret Boundary

Job files may contain credential reference names, but must not contain passwords, tokens, SAS values, or secret material.
