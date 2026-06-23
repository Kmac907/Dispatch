# Dispatch Credentials

Dispatch uses credential references so jobs and inventories can name a credential without embedding plaintext secrets.

The canonical credential store and resolution design is [Credential Store Plan](credential-store-plan.md). That file defines the global `C:\ProgramData\Dispatch\config.yml` credential catalog, provider settings, enrollment behavior, Azure Key Vault auth modes, and precedence rules.

Current implementation status:

- `dispatch creds add|list|test|remove` is wired to a credential provider abstraction.
- The default provider is `none` and reports unavailable.
- Dispatch loads the global YAML config from `C:\ProgramData\Dispatch\config.yml` by default when it exists. `dispatch run ps|cmd|exe --config <path>` also accepts YAML config for the current run defaults.
- Config-defined `credentials:<name>` entries are available to `dispatch creds list|test|add|remove`; `prompt` and non-secret catalog entries remain metadata-only, `dpapi_file` and `windows_credential_manager` can enroll, test, and remove their configured protected secret locations, and `azure_keyvault` can validate its configured secret reference without storing a local password.
- Config-defined credential references are validated against the selected provider's required metadata before `creds test`, `creds add`, or `run ps|cmd|exe --credential <name>` accepts them.
- `dispatch run ps|cmd|exe --credential <name>` accepts a credential reference override, validates it against the configured credential provider before planning endpoint work, and applies that reference to all selected targets.
- `provider: prompt` references can resolve at runtime for real `--transport psrp` execution. Dispatch prompts for the configured username's password before live rendering starts, keeps the password in memory only, passes it to PSRP as a `PSCredential`, and disposes it after the run.
- `provider: dpapi_file` references can now be enrolled with `dispatch creds add <name>`, validated with `dispatch creds test <name>`, removed with `dispatch creds remove <name>`, and resolved at runtime for real `--transport psrp` execution. Dispatch stores a Windows DPAPI CurrentUser-protected file at the configured `path`; `creds add` refuses an existing file unless `--force` is supplied.
- `provider: windows_credential_manager` references can now be enrolled with `dispatch creds add <name>`, validated with `dispatch creds test <name>`, removed with `dispatch creds remove <name>`, and resolved at runtime for real `--transport psrp` execution. Dispatch stores the secret in Windows Credential Manager at the configured `target`; `creds add` refuses an existing target unless `--force` is supplied.
- `provider: azure_keyvault` references can now be validated with `dispatch creds add <name>` or `dispatch creds test <name>` and resolved at runtime for real `--transport psrp` execution. Dispatch authenticates to Azure using the configured `auth` mode, reads the configured Key Vault secret into memory, passes it to PSRP as a `PSCredential`, and disposes it after the run. It does not store the endpoint password locally.
- Configuring `Dispatch:CredentialProvider` as `file` or `local` enables a file-backed reference catalog at `Dispatch:CredentialStorePath`, defaulting to `C:\ProgramData\Dispatch\Credentials\references.json`.
- No plaintext password command-line flags are supported.
- No credential secret is stored by the default provider or by the file-backed provider. The file-backed provider stores reference names and optional username metadata only.
- YAML inventories in the current supported subset accept `credential: <name>` reference names on defaults, group vars, host vars, and hosts.
- Direct CLI `--credential <name>` overrides inventory credential references for the current ad-hoc run path.
- YAML inventory validation rejects plaintext secret-like fields such as `password`, `secret`, `token`, `sas`, `sasToken`, and fields ending in `Password`, `Secret`, or `Token`.
- YAML config loading rejects direct plaintext secret keys such as `password`, `secret`, `token`, and `sas`.
- Runtime endpoint credential resolution is currently limited to prompt-provider, DPAPI-file, Windows Credential Manager, and Azure Key Vault PSRP handoff. `pscredential` wrapper handoff, raw WinRM handoff, PsExec handoff, and YAML job credential validation are later slices. This is distinct from runtime script secret handoff for SAS, Blob, or Key Vault payload secrets.

Examples:

```powershell
dispatch creds list
dispatch creds add prod-admin --username CONTOSO\Admin
dispatch creds add helpdesk-local --force
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

For `provider: prompt`, `dispatch creds add prod-admin` performs no secret enrollment and reports that the credential will prompt at runtime. Runtime prompting currently applies to real PSRP execution only; `--plan` and dry-run output do not prompt or resolve passwords.

For `provider: dpapi_file`, `dispatch creds add helpdesk-local` securely prompts for the password twice, writes a DPAPI CurrentUser-protected file at the configured `path`, and does not write the plaintext password to YAML, stdout, stderr, durable run logs, or result files. `dispatch creds test helpdesk-local` verifies that the configured file exists, matches the configured reference and username, and can be decrypted by the current Windows user. `dispatch creds remove helpdesk-local` deletes the protected file but leaves the config reference in `config.yml`.

For `provider: windows_credential_manager`, `dispatch creds add domain-admin` securely prompts for the password twice, writes a generic Windows Credential Manager entry at the configured `target`, and does not write the plaintext password to YAML, stdout, stderr, durable run logs, or result files. `dispatch creds test domain-admin` verifies that the configured target exists, matches the configured username, and can be read by the current Windows user. `dispatch creds remove domain-admin` deletes the Windows Credential Manager target but leaves the config reference in `config.yml`.

For `provider: azure_keyvault`, `dispatch creds add kv-prod-admin` and `dispatch creds test kv-prod-admin` authenticate to Azure using the configured `auth` mode and verify that the configured secret can be read. No endpoint password is prompted for or stored locally. Runtime PSRP execution reads the secret into memory and disposes it after the run.

Provider metadata validation currently requires:

| Provider | Required metadata |
| --- | --- |
| `prompt` | `username` |
| `pscredential` | `username` |
| `dpapi_file` | `username`, `path` |
| `windows_credential_manager` | `username`, `target` |
| `azure_keyvault` | `username`, `vault_uri`, `secret_name`, `auth` |

`azure_keyvault.auth` must be one of `default_azure_credential`, `managed_identity`, or `azure_cli`, and `vault_uri` must be an absolute `https://` URI. `creds add`, `creds test`, and real PSRP runtime resolution read the configured Key Vault secret through the Azure SDK; dry-run/plan output does not resolve the secret.

The file-backed `file` / `local` provider writes a JSON catalog containing reference names and optional usernames. It does not prompt for, store, encrypt, decrypt, or hand off passwords. The `dpapi_file` provider is separate from that metadata catalog and stores one DPAPI-protected file per configured credential reference path. The `windows_credential_manager` provider stores one generic Windows Credential Manager target per configured credential reference target.

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

Reference names are metadata in plans and logs. Inventory references are carried through target resolution, and a direct `--credential <name>` override replaces the inventory-selected reference for the current ad-hoc run. For real PSRP runs with `provider: prompt`, `provider: dpapi_file`, `provider: windows_credential_manager`, or `provider: azure_keyvault`, Dispatch resolves the referenced password before execution and passes an in-memory `PSCredential` to command, script, and artifact PSRP sessions.

Security boundary:

- Do not pass passwords, SAS tokens, or other secrets on the command line.
- Credential reference names may appear in output and logs.
- Secret values must not appear in console output, JSON, NDJSON, YAML, durable logs, or dry-run output.
