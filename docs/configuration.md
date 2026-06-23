# Configuration

Dispatch uses one global machine-wide YAML config plus explicit command overrides.

Default path:

```text
C:\ProgramData\Dispatch\config.yml
```

`--config <path>` can override the default for testing, alternate environments, or non-default locations.

## Config Loading Precedence

1. Explicit `--config <path>`.
2. `C:\ProgramData\Dispatch\config.yml`.
3. Built-in defaults.

## Runtime Option Precedence

Explicit CLI options override job, inventory, config, and built-in defaults for the current command.

## Example

```yaml
dispatch:
  default_transport: psrp
  default_credential_provider: prompt
  run_root: C:\ProgramData\Dispatch\Runs
  remote_run_root: C:\ProgramData\Dispatch\Runs

credentials:
  prod-admin:
    provider: prompt
    username: CONTOSO\prod.admin

  helpdesk-local:
    provider: dpapi_file
    username: .\helpdesk-admin
    path: C:\ProgramData\Dispatch\Credentials\helpdesk-local.cred
```

## Supported Keys

`dispatch` keys:

- `default_transport`
- `default_credential_provider`
- `credential_store_path` for legacy metadata catalog support where still present
- `run_root`
- `remote_run_root`
- throttle/concurrency defaults where implemented

`credentials.<name>` keys depend on provider:

- `provider`
- `username`
- `path`
- `target`
- `vault_uri`
- `secret_name`
- `auth`

## Credential Rule

`job.yml`, `hosts.yml`, and `--credential <name>` select a credential reference. The global config defines what that reference means and how it is resolved.

No passwords in YAML.
