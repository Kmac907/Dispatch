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

For credentials, the CLI selects a reference; the loaded config defines the provider metadata for that reference. The CLI should not accept endpoint passwords as command-line arguments.

## Example

```yaml
dispatch:
  default_transport: psrp
  default_credential_provider: prompt
  local_run_root: C:\ProgramData\Dispatch\Runs
  remote_run_root: C:\ProgramData\Dispatch\Runs
  allow_run_as_system: false

credentials:
  prod-admin:
    provider: prompt
    username: CONTOSO\prod.admin

  helpdesk-local:
    provider: dpapi_file
    username: .\helpdesk-admin
    path: C:\ProgramData\Dispatch\Credentials\helpdesk-local.cred

  domain-admin:
    provider: windows_credential_manager
    username: CONTOSO\domain.admin
    target: Dispatch/domain-admin

  kv-prod-admin:
    provider: azure_keyvault
    username: CONTOSO\prod.admin
    vault_uri: https://contoso-dispatch-kv.vault.azure.net/
    secret_name: prod-admin-password
    auth: default_azure_credential
```

## Supported Keys

`dispatch` keys:

- `default_transport`
- `default_credential_provider`
- `credential_store_path` for legacy metadata catalog support where still present
- `local_run_root`
- `remote_run_root`
- `allow_run_as_system` enables PsExec LocalSystem execution when an operator also supplies `--system`; default is `false`
- throttle/batching defaults where implemented

`credentials.<name>` keys depend on provider:

- `provider`
- `username`
- `path`
- `target`
- `vault_uri`
- `secret_name`
- `auth`

## Example With Inventory

`C:\ProgramData\Dispatch\config.yml`:

```yaml
dispatch:
  default_transport: psrp

credentials:
  prod-admin:
    provider: prompt
    username: CONTOSO\prod.admin
```

`hosts.yml`:

```yaml
defaults:
  transport: psrp
  credential: prod-admin
hosts:
  PC001:
  PC002:
```

Command:

```powershell
dispatch run ps .\Fix.ps1 --inventory .\hosts.yml --target PC001
```

Dispatch loads the global config, resolves `prod-admin`, prompts for the configured username at runtime, and keeps the password in memory only.

## Credential Rule

`job.yml`, `hosts.yml`, and `--credential <name>` select a credential reference. The global config defines what that reference means and how it is resolved.

No passwords in YAML.

Secret-like keys such as `password`, `secret`, `token`, and `sas` are rejected in Dispatch YAML inputs even when the value appears to be placeholder text.
