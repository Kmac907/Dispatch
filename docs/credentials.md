# Dispatch Credentials

Dispatch uses credential references so jobs and inventories can name a credential without embedding plaintext secrets.

The canonical credential store and resolution design is [Credential Store Plan](credential-store-plan.md). That file defines the global `C:\ProgramData\Dispatch\config.yml` credential catalog, provider settings, enrollment behavior, Azure Key Vault auth modes, and precedence rules.

Current implementation status:

- `dispatch creds add|list|test|remove` is wired to a credential provider abstraction.
- The default provider is `none` and reports unavailable.
- Dispatch loads the global YAML config from `C:\ProgramData\Dispatch\config.yml` by default when it exists. `dispatch run ps|cmd|exe --config <path>` also accepts YAML config for the current run defaults.
- Config-defined `credentials:<name>` entries are available to `dispatch creds list|test|add|remove` as a metadata-only catalog.
- Config-defined credential references are validated against the selected provider's required metadata before `creds test`, `creds add`, or `run ps|cmd|exe --credential <name>` accepts them.
- `dispatch run ps|cmd|exe --credential <name>` accepts a credential reference override, validates it against the configured credential provider before planning endpoint work, and applies that reference to all selected targets.
- Configuring `Dispatch:CredentialProvider` as `file` or `local` enables a file-backed reference catalog at `Dispatch:CredentialStorePath`, defaulting to `C:\ProgramData\Dispatch\Credentials\references.json`.
- No plaintext password command-line flags are supported.
- No credential secret is stored by the default provider or by the file-backed provider. The file-backed provider stores reference names and optional username metadata only.
- YAML inventories in the current supported subset accept `credential: <name>` reference names on defaults, group vars, host vars, and hosts.
- Direct CLI `--credential <name>` overrides inventory credential references for the current ad-hoc run path.
- YAML inventory validation rejects plaintext secret-like fields such as `password`, `secret`, `token`, `sas`, `sasToken`, and fields ending in `Password`, `Secret`, or `Token`.
- YAML config loading rejects direct plaintext secret keys such as `password`, `secret`, `token`, and `sas`.
- YAML job credential validation, runtime endpoint credential resolution, and transport credential handoff are later slices. This is distinct from runtime script secret handoff for SAS, Blob, or Key Vault payload secrets.

Examples:

```powershell
dispatch creds list
dispatch creds add prod-admin --username CONTOSO\Admin
dispatch creds test prod-admin
dispatch creds remove prod-admin
dispatch run ps .\Fix.ps1 --target WEB01 --credential prod-admin --plan
```

For automation, use structured output:

```powershell
dispatch creds list --output json
```

When no provider is configured, the command exits nonzero and reports provider availability. Structured output still includes the provider name, availability state, success state, message, and any references returned by the provider.

To enable local reference metadata storage in the global Dispatch config:

```yaml
dispatch:
  default_credential_provider: file
  credential_store_path: C:\ProgramData\Dispatch\Credentials\references.json
```

To define metadata-only credential references in the global Dispatch config:

```yaml
dispatch:
  default_credential_provider: prompt

credentials:
  prod-admin:
    provider: prompt
    username: CONTOSO\prod.admin
```

For `provider: prompt`, `dispatch creds add prod-admin` performs no secret enrollment and reports that the credential will prompt at runtime once runtime prompting is implemented.

Provider metadata validation currently requires:

| Provider | Required metadata |
| --- | --- |
| `prompt` | `username` |
| `pscredential` | `username` |
| `dpapi_file` | `username`, `path` |
| `windows_credential_manager` | `username`, `target` |
| `azure_keyvault` | `username`, `vault_uri`, `secret_name`, `auth` |

`azure_keyvault.auth` must be one of `default_azure_credential`, `managed_identity`, or `azure_cli`, and `vault_uri` must be an absolute `https://` URI. This validation does not retrieve a Key Vault secret or store a local secret yet.

The file-backed provider writes a JSON catalog containing reference names and optional usernames. It does not prompt for, store, encrypt, decrypt, or hand off passwords.

Inventory reference behavior:

```yaml
defaults:
  credential: prod-default
groups:
  web:
    vars:
      credential: web-admin
    hosts: [WEB01]
hosts:
  WEB01:
    credential: host-admin
```

Reference names are metadata only in the current slice. Inventory references are carried through target resolution, and a direct `--credential <name>` override replaces the inventory-selected reference for the current ad-hoc run. No password is retrieved or handed to transports yet.

Security boundary:

- Do not pass passwords, SAS tokens, or other secrets on the command line.
- Credential reference names may appear in output and logs.
- Secret values must not appear in console output, JSON, NDJSON, YAML, durable logs, or dry-run output.
