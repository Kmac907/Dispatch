# Dispatch Credentials

Dispatch uses credential references so jobs, inventories, and CLI overrides can select endpoint credentials without embedding plaintext secrets.

Reference names are safe to log. Resolved passwords are never serialized and must not appear in YAML, command-line arguments, console output, run logs, result JSON, NDJSON, CSV, or artifacts.

## Endpoint Credentials Only

`--credential <name>` selects the account used by a transport to authenticate to the endpoint. It is not a script input mechanism and must not be used for SAS tokens, API keys, installer secrets, or payload-download secrets.

Script secret handoff is a separate surface:

```powershell
dispatch run ps .\Install-App.ps1 --target PC001 --secret packageSas=prod-package-sas --plan --output json
```

The default script secret handoff design is script parameter binding. The script declares a matching parameter such as `param([string]$packageSas)`. Current support validates the option shape and renders redacted parameter bindings such as `-packageSas [redacted]` in plan and dry-run output without resolving or printing secret values. Roadmap 10 owns runtime provider resolution and safe transport parameter binding.

## Global Credential Catalog

Credential provider metadata lives in the global Dispatch config:

```text
C:\ProgramData\Dispatch\config.yml
```

Example:

```yaml
dispatch:
  default_transport: psrp
  default_credential_provider: prompt

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

Job files and inventory files may reference those names, but they do not define provider details.

## Reference Selection

Credential reference precedence:

1. CLI `--credential <name>`.
2. `job.yml` credential.
3. Host credential.
4. Group credential.
5. Inventory defaults credential.
6. No credential.

Credential metadata lookup always comes from the loaded Dispatch config.

## Providers

| Provider | Purpose | Enrollment behavior |
| --- | --- | --- |
| `prompt` | Prompt securely at runtime. | No secret enrollment; `creds add` reports that runtime prompting will be used. |
| `pscredential` | PowerShell module wrapper handoff only. | Invalid from direct `dispatch.exe`; the wrapper uses supplied `-Credential` or prompts with `Get-Credential` when `-Credential` is omitted. |
| `dpapi_file` | Local Windows DPAPI-protected file. | Prompts, confirms, protects with DPAPI, writes the configured file, and restricts file ACLs to the current Windows user, local Administrators, and LocalSystem. |
| `windows_credential_manager` | Windows Credential Manager generic credential. | Prompts, confirms, and writes the configured target. |
| `azure_keyvault` | Azure Key Vault secret retrieval for the endpoint password. | Validates Key Vault access and secret readability; no local password storage. |

Provider metadata requirements:

| Provider | Required metadata |
| --- | --- |
| `prompt` | `username` |
| `pscredential` | `username` |
| `dpapi_file` | `username`, `path` |
| `windows_credential_manager` | `username`, `target` |
| `azure_keyvault` | `username`, `vault_uri`, `secret_name`, `auth` |

## Commands

```powershell
dispatch creds list
dispatch creds add prod-admin
dispatch creds test prod-admin
dispatch creds remove prod-admin
```

Use `--force` when overwriting an existing DPAPI file or Windows Credential Manager target.

DPAPI file enrollment disables inherited ACLs on the protected file. The file remains local to the configured path and is readable only by the current Windows user that enrolled it, local Administrators, and LocalSystem.

## Runtime Use

```powershell
dispatch run ps .\Fix.ps1 --target PC001 --transport psrp --credential prod-admin
dispatch run cmd whoami --target PC001 --transport winrm --credential prod-admin
dispatch run ps .\Fix.ps1 --inventory .\hosts.yml --target kiosks
dispatch apply .\job.yml --credential breakglass-admin
```

Runtime credential resolution is implemented for PSRP and raw WinRM execution. Plan and dry-run paths validate references but do not prompt, decrypt DPAPI files, read Windows Credential Manager targets, or read Key Vault secrets.

`provider: pscredential` is reserved for PowerShell module execution wrappers, not direct `dispatch.exe`. The current module exposes diagnostic/version wrappers plus `Invoke-DispatchPowerShell` with `-CredentialName <name>` passthrough to the existing CLI `--credential <name>` reference, but `pscredential` runtime handoff remains planned under Roadmap `7`. In that later handoff slice, if `-Credential <PSCredential>` is supplied, the module uses that live object; if it is omitted, the module prompts with `Get-Credential`, using the configured username when present. Direct `dispatch.exe --credential <name>` must reject `pscredential` unless a compatible protected wrapper handoff is present. `provider: prompt` stays Dispatch-owned, so the module should let Dispatch perform the prompt instead of prompting itself.

Script secrets follow their own `--secret name=reference` model. Current support is plan/dry-run redacted parameter rendering only. In the future Roadmap 10 final pass-off flow, Dispatch resolves `reference` from the configured secret provider on the admin side and binds it to the script parameter named by `name`. Secret values must never be passed as ordinary command-line script arguments or serialized into Dispatch logs or results.

## Azure Key Vault Auth

`auth` controls how Dispatch authenticates to Azure Key Vault. It is not the endpoint credential password.

Planned values:

- `default_azure_credential`
- `managed_identity`
- `azure_cli`

`default_azure_credential` uses the Azure SDK default chain, such as Azure CLI login, Azure PowerShell login, Visual Studio sign-in, workload identity, managed identity, or environment credentials depending on the machine.

Use `managed_identity` for production hosts where Dispatch runs under an Azure-managed identity. Use `default_azure_credential` for early admin-workstation testing.

## YAML Rules

Reject secret values in all YAML files:

```yaml
password: anything
secret: anything
token: anything
sas: anything
credential:
  password: anything
```

Allowed non-secret metadata:

```yaml
credential: prod-admin
username: CONTOSO\prod.admin
path: C:\ProgramData\Dispatch\Credentials\helpdesk-local.cred
target: Dispatch/domain-admin
vault_uri: https://contoso-dispatch-kv.vault.azure.net/
secret_name: prod-admin-password
auth: default_azure_credential
```
