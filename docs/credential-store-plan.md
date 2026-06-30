# Credential Store Plan

This document is the canonical credential store and credential resolution design for Dispatch. It applies to the direct `dispatch.exe` command surface and to the planned PowerShell module wrapper.

## Scope Distinction

This plan covers endpoint credentials selected by credential references such as `credential: prod-admin` or CLI `--credential prod-admin`.

It does not make Dispatch responsible for script secret handoff, SAS token delivery, Blob payload retrieval, or Azure Key Vault payload-secret retrieval. Those are separate concerns from endpoint authentication.

The approved script secret handoff surface is separate:

```powershell
dispatch run ps .\Install-App.ps1 --target PC001 --secret packageSas=prod-package-sas
```

`--credential <name>` selects the endpoint credential. `--secret name=reference` selects a script secret reference. `name` becomes the PowerShell script parameter, so the script declares `param([string]$packageSas)` and plan output renders `-packageSas [redacted]`. Current support validates the option shape and renders redacted plan/dry-run output without resolving `reference`. Roadmap 10 owns real execution: Dispatch must resolve `reference` from a configured secret provider on the admin side and bind the resolved value to the script parameter through the selected transport. Secret values must not be placed on the command line, in logs, in results, in traces, or in artifacts.

## Core Principle

There is one global Dispatch config file. It is not credential-specific.

```text
C:\ProgramData\Dispatch\config.yml
```

This is the machine-wide Dispatch config referenced by the CLI design. Dispatch should load it by default. `--config <path>` is only an override for testing, alternate environments, or non-default config locations.

Credential references may appear only where they are actually used:

- `job.yml` if the job chooses the credential
- `hosts.yml` if the inventory assigns one credential to hosts, groups, or defaults
- CLI `--credential` only as an override

Provider settings, credential metadata, store locations, Key Vault URIs, Key Vault auth modes, secret names, and usernames live in one place:

```text
C:\ProgramData\Dispatch\config.yml
```

## Credential Rule

```text
job.yml / hosts.yml / CLI:
  says which credential reference to use

global Dispatch config.yml:
  defines what that reference means and where/how to resolve it
```

No passwords in YAML. Ever.

## Supported Providers

1. `prompt`
2. `pscredential`, PowerShell module wrapper only
3. `dpapi_file`
4. `windows_credential_manager`
5. `azure_keyvault`

## Global Dispatch Config

Default machine-wide path:

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

This file is the credential reference catalog and provider-location catalog.

Meaning:

```text
prod-admin       -> prompt user for CONTOSO\prod.admin password
helpdesk-local   -> load DPAPI-protected file
domain-admin     -> load Windows Credential Manager target
kv-prod-admin    -> retrieve secret from Azure Key Vault
```

## Azure Key Vault Auth Modes

`auth` controls how Dispatch authenticates to Azure Key Vault. It is not the endpoint credential password.

Example:

```yaml
credentials:
  kv-prod-admin:
    provider: azure_keyvault
    username: CONTOSO\prod.admin
    vault_uri: https://contoso-dispatch-kv.vault.azure.net/
    secret_name: prod-admin-password
    auth: default_azure_credential
```

Meaning:

```text
Use Azure SDK DefaultAzureCredential to authenticate to Key Vault.
Then read secret prod-admin-password.
Use that secret as the password for CONTOSO\prod.admin.
```

Planned auth values:

```yaml
auth: default_azure_credential
auth: managed_identity
auth: azure_cli
```

`default_azure_credential`:

```text
Uses Azure SDK DefaultAzureCredential.
May authenticate from environment variables, workload identity, managed identity,
Visual Studio, VS Code, Azure CLI, Azure PowerShell, Azure Developer CLI,
or other supported Azure SDK credential-chain sources.
```

Admin workstation example:

```powershell
az login
```

or:

```powershell
Connect-AzAccount
```

Config:

```yaml
auth: default_azure_credential
```

Azure host / production example:

```yaml
auth: managed_identity
```

System-assigned managed identity:

```yaml
credentials:
  kv-prod-admin:
    provider: azure_keyvault
    username: CONTOSO\prod.admin
    vault_uri: https://contoso-dispatch-kv.vault.azure.net/
    secret_name: prod-admin-password
    auth: managed_identity
```

User-assigned managed identity:

```yaml
credentials:
  kv-prod-admin:
    provider: azure_keyvault
    username: CONTOSO\prod.admin
    vault_uri: https://contoso-dispatch-kv.vault.azure.net/
    secret_name: prod-admin-password
    auth: managed_identity
    managed_identity_client_id: 00000000-0000-0000-0000-000000000000
```

Azure CLI explicit mode:

```yaml
credentials:
  kv-prod-admin:
    provider: azure_keyvault
    username: CONTOSO\prod.admin
    vault_uri: https://contoso-dispatch-kv.vault.azure.net/
    secret_name: prod-admin-password
    auth: azure_cli
```

The Azure identity used by Dispatch must be able to read the Key Vault secret, for example through Key Vault RBAC such as Key Vault Secrets User.

Recommendation:

```text
Use default_azure_credential for early admin workstation testing.
Use managed_identity for production machine-wide Dispatch hosts when available.
Use azure_cli only when an operator intentionally wants Dispatch tied to az login state.
```

## Host File

Use `hosts.yml` when all hosts, a group, or a specific host should use a credential.

Example:

```yaml
defaults:
  transport: psrp
  credential: prod-admin

groups:
  kiosks:
    vars:
      credential: helpdesk-local
    hosts:
      - KIOSK01
      - KIOSK02

hosts:
  SERVER01:
    credential: domain-admin
  SERVER02:
```

This file only references names from the global Dispatch config.

It does not define provider, username, vault URI, Key Vault auth mode, paths, or passwords.

## Job File

Use `job.yml` when the job chooses the credential.

Example:

```yaml
name: Fix endpoints
hosts: SERVER01
transport: psrp
credential: kv-prod-admin

tasks:
  - ps: .\Fix.ps1
```

This means:

```text
Use credential reference kv-prod-admin from C:\ProgramData\Dispatch\config.yml.
```

`job.yml` does not define provider details.

## Commands

Normal job run using default global config:

```powershell
dispatch apply .\job.yml
```

Job run with explicit config override:

```powershell
dispatch apply .\job.yml --config .\config.yml
```

Ad-hoc run using inventory credential and default global config:

```powershell
dispatch run ps .\Fix.ps1 --inventory .\hosts.yml --target kiosks
```

Ad-hoc run with explicit config override:

```powershell
dispatch run ps .\Fix.ps1 --inventory .\hosts.yml --target kiosks --config .\config.yml
```

Override credential from CLI:

```powershell
dispatch apply .\job.yml --credential breakglass-admin
```

The CLI override still must reference a credential defined in the loaded global Dispatch config.

## Precedence

Credential reference selection:

```text
CLI --credential
> job.yml credential
> hosts.yml host credential
> hosts.yml group credential
> hosts.yml defaults credential
> no credential
```

Credential metadata lookup:

```text
Always loaded Dispatch config.yml credentials.<name>
```

Provider defaults:

```text
credentials.<name>.provider
> dispatch.default_credential_provider
> prompt
```

Config loading precedence:

```text
explicit --config <path>
> C:\ProgramData\Dispatch\config.yml
> built-in defaults
```

Environment overrides can be added later, but they should not create a separate credential config model.

## Prompt Provider

Config:

```yaml
credentials:
  prod-admin:
    provider: prompt
    username: CONTOSO\prod.admin
```

Runtime:

```powershell
dispatch apply .\job.yml
```

Prompt:

```text
Credential: prod-admin
Username: CONTOSO\prod.admin
Password:
```

Password is kept in memory only and discarded after the run.

Current implementation:

- Implemented for real `dispatch run ps|cmd|exe ... --transport psrp` and `--transport winrm` execution.
- The prompt happens after planning and before live rendering or endpoint work starts.
- `--plan` / dry-run paths validate and carry the reference name but do not prompt.
- The resolved password is converted to an in-memory PSRP `PSCredential` for PSRP command, script, and artifact sessions.
- For raw WinRM, the resolved password is passed into WSMan connection options for upload, execution, and artifact-download shells.
- PsExec, YAML `apply`, and PowerShell-wrapper `pscredential` handoff remain separate boundaries.

## PSCredential Provider

PowerShell module wrapper only.

Config:

```yaml
credentials:
  prod-admin:
    provider: pscredential
    username: CONTOSO\prod.admin
```

PowerShell:

```powershell
Invoke-DispatchPowerShell `
  -Script .\Fix.ps1 `
  -Target PC001 `
  -CredentialName prod-admin `
  -Credential $cred
```

The `-Credential` parameter is optional. If it is omitted and `prod-admin` resolves to `provider: pscredential`, the module prompts with `Get-Credential`, using the configured username when present:

```powershell
Invoke-DispatchPowerShell `
  -Script .\Fix.ps1 `
  -Target PC001 `
  -CredentialName prod-admin
```

Rules:

- Only valid through the PowerShell wrapper.
- Direct `dispatch.exe` must reject `provider: pscredential` unless a compatible protected wrapper handoff is present.
- `-CredentialName <name>` selects the configured credential reference.
- `-Credential <PSCredential>` supplies a live credential object, but is not required.
- If `-CredentialName` resolves to `provider: pscredential` and `-Credential` is omitted, the module calls `Get-Credential`, using the configured username when available.
- If `-CredentialName` resolves to `provider: pscredential` and `-Credential` is supplied, the module uses the supplied object and must validate or explicitly document username mismatch behavior when the config also has `username`.
- If `-CredentialName` resolves to `provider: prompt`, the module does not call `Get-Credential`; Dispatch performs its normal runtime prompt.
- If `-CredentialName` resolves to `dpapi_file`, `windows_credential_manager`, or `azure_keyvault`, the module does not prompt; Dispatch resolves the provider normally.
- Nothing is stored.
- Password never becomes a command-line argument.

## DPAPI File Provider

Config:

```yaml
credentials:
  helpdesk-local:
    provider: dpapi_file
    username: .\helpdesk-admin
    path: C:\ProgramData\Dispatch\Credentials\helpdesk-local.cred
```

Enrollment:

```powershell
dispatch creds add helpdesk-local
```

Dispatch securely prompts for the password and writes a DPAPI-protected file to the configured path.

Run:

```powershell
dispatch run ps .\Fix.ps1 --target KIOSK01 --credential helpdesk-local
```

Current implementation:

- Implemented for direct `dispatch.exe` config-defined `dpapi_file` references.
- `dispatch creds add <name>` prompts for password and confirmation, then writes a Windows DPAPI CurrentUser-protected file to the configured `path`.
- The protected file ACL is hardened after enrollment: inheritance is disabled, and only the current Windows user, local Administrators, and LocalSystem receive file access.
- `dispatch creds add <name>` refuses to overwrite an existing file unless `--force` is supplied.
- `dispatch creds test <name>` verifies that the file exists, matches the configured reference and username, and can be decrypted by the current Windows user.
- `dispatch creds remove <name>` deletes the configured protected file but leaves the reference in `C:\ProgramData\Dispatch\config.yml`.
- Real `dispatch run ps|cmd|exe ... --transport psrp` execution can resolve the configured DPAPI file into an in-memory PSRP `PSCredential`.
- Real `dispatch run ps|cmd|exe ... --transport winrm` execution can resolve the configured DPAPI file and pass it into WSMan connection options.
- PsExec, YAML `apply`, and PowerShell-wrapper `pscredential` handoff remain separate boundaries.

## Windows Credential Manager Provider

Config:

```yaml
credentials:
  domain-admin:
    provider: windows_credential_manager
    username: CONTOSO\domain.admin
    target: Dispatch/domain-admin
```

Enrollment:

```powershell
dispatch creds add domain-admin
```

Dispatch securely prompts and stores the credential in Windows Credential Manager using the configured target.

Current implementation:

- Implemented for direct `dispatch.exe` config-defined `windows_credential_manager` references.
- `dispatch creds add <name>` prompts for password and confirmation, then writes a generic Windows Credential Manager entry to the configured `target`.
- `dispatch creds add <name>` refuses to overwrite an existing target unless `--force` is supplied.
- `dispatch creds test <name>` verifies that the target exists, matches the configured username, and can be read by the current Windows user.
- `dispatch creds remove <name>` deletes the configured Windows Credential Manager target but leaves the reference in `C:\ProgramData\Dispatch\config.yml`.
- Real `dispatch run ps|cmd|exe ... --transport psrp` execution can resolve the configured Windows Credential Manager target into an in-memory PSRP `PSCredential`.
- Real `dispatch run ps|cmd|exe ... --transport winrm` execution can resolve the configured Windows Credential Manager target and pass it into WSMan connection options.
- PsExec, YAML `apply`, and PowerShell-wrapper `pscredential` handoff remain separate boundaries.

## Azure Key Vault Provider

Config:

```yaml
credentials:
  kv-prod-admin:
    provider: azure_keyvault
    username: CONTOSO\prod.admin
    vault_uri: https://contoso-dispatch-kv.vault.azure.net/
    secret_name: prod-admin-password
    auth: default_azure_credential
```

Run:

```powershell
dispatch apply .\job.yml
```

Dispatch retrieves the secret from:

```text
https://contoso-dispatch-kv.vault.azure.net/secrets/prod-admin-password
```

Then builds an in-memory credential object for the transport.

Current implementation:

- Implemented for direct `dispatch.exe` config-defined `azure_keyvault` references.
- `dispatch creds add <name>` does not prompt for or store an endpoint password locally; it authenticates to Azure using the configured `auth` mode and validates that the configured secret can be read.
- `dispatch creds test <name>` performs the same readability validation.
- `dispatch creds remove <name>` does not remove Azure Key Vault secrets; the config reference must be removed from `C:\ProgramData\Dispatch\config.yml`, and any vault secret changes must be made through Azure tooling or a future explicit command.
- Real `dispatch run ps|cmd|exe ... --transport psrp` execution can resolve the configured Key Vault secret into an in-memory PSRP `PSCredential`.
- Real `dispatch run ps|cmd|exe ... --transport winrm` execution can resolve the configured Key Vault secret and pass it into WSMan connection options.
- PsExec, YAML `apply`, PowerShell-wrapper `pscredential` handoff, and creating/updating Key Vault secrets through Dispatch remain separate boundaries.

## dispatch creds Behavior

All credential references are defined in the loaded global Dispatch config.

```powershell
dispatch creds list
```

Lists references from:

```yaml
credentials:
  prod-admin:
  helpdesk-local:
  domain-admin:
  kv-prod-admin:
```

Test a credential:

```powershell
dispatch creds test prod-admin
```

Add/enroll a credential secret for providers that store secrets:

```powershell
dispatch creds add helpdesk-local
dispatch creds add domain-admin
```

`dispatch creds add <name>` always starts by loading the global Dispatch config and finding:

```yaml
credentials:
  <name>:
    provider: ...
```

If `<name>` is not defined in config, fail.

## Enrollment Behavior By Provider

### prompt

Config:

```yaml
credentials:
  prod-admin:
    provider: prompt
    username: CONTOSO\prod.admin
```

Enrollment behavior:

```text
No secret enrollment.
No password is stored.
Return: "No enrollment required. Credential prod-admin will prompt at runtime."
```

Recommended exit: `0`.

### pscredential

Config:

```yaml
credentials:
  prod-admin:
    provider: pscredential
    username: CONTOSO\prod.admin
```

Enrollment behavior from direct `dispatch.exe`:

```text
Invalid operation.
PSCredential credentials are supplied by the PowerShell wrapper at runtime.
```

Recommended exit: `1`.

PowerShell wrapper does not need `creds add` for this provider.

### dpapi_file

Config:

```yaml
credentials:
  helpdesk-local:
    provider: dpapi_file
    username: .\helpdesk-admin
    path: C:\ProgramData\Dispatch\Credentials\helpdesk-local.cred
```

Enrollment behavior:

```text
Prompt securely for password.
Confirm password.
Protect password using Windows DPAPI.
Write protected blob to configured path.
Set file ACLs restrictively.
Return success.
```

Recommended exit: `0`.

If file exists:

```text
Refuse by default.
Use --force to overwrite.
```

### windows_credential_manager

Config:

```yaml
credentials:
  domain-admin:
    provider: windows_credential_manager
    username: CONTOSO\domain.admin
    target: Dispatch/domain-admin
```

Enrollment behavior:

```text
Prompt securely for password.
Confirm password.
Write credential to Windows Credential Manager under configured target.
Return success.
```

Recommended exit: `0`.

If target exists:

```text
Refuse by default.
Use --force to overwrite.
```

### azure_keyvault

Config:

```yaml
credentials:
  kv-prod-admin:
    provider: azure_keyvault
    username: CONTOSO\prod.admin
    vault_uri: https://contoso-dispatch-kv.vault.azure.net/
    secret_name: prod-admin-password
    auth: default_azure_credential
```

Enrollment behavior:

```text
Do not prompt for endpoint password.
Do not store anything locally.
Authenticate to Azure using configured auth mode.
Validate vault is reachable.
Validate secret exists.
Optionally validate secret can be read.
Return success if reference is usable.
```

Recommended exit: `0` if validation succeeds.

If the goal is to create/update the Key Vault secret through Dispatch, that should be a separate explicit command later, not default `creds add`, for example:

```powershell
dispatch creds set-secret kv-prod-admin
```

Not part of the first Key Vault implementation.

### unknown provider

Config:

```yaml
credentials:
  bad-ref:
    provider: something_else
```

Enrollment behavior:

```text
Fail with unsupported provider.
```

Recommended exit: `1`.

Summary:

```text
prompt:
  add = no-op validation, runtime prompt later

pscredential:
  add = invalid from dispatch.exe

dpapi_file:
  add = prompt and store DPAPI-protected file
  test = verify file exists, reference/username matches config, and current Windows user can decrypt it
  remove = delete configured protected file, leaving config.yml reference intact
  runtime = implemented for PSRP and raw WinRM handoff

windows_credential_manager:
  add = prompt and store in Windows Credential Manager
  test = verify target exists, username matches config, and current Windows user can read it
  remove = delete configured target, leaving config.yml reference intact
  runtime = implemented for PSRP and raw WinRM handoff

azure_keyvault:
  add = validate Azure secret reference, no local storage
  test = validate Azure secret reference readability
  remove = invalid from dispatch.exe; remove the config reference or manage the vault secret outside Dispatch
  runtime = implemented for PSRP and raw WinRM handoff
```

## Validation Rules

Reject secret values in all YAML files:

```yaml
password: anything
secret: anything
token: anything
sas: anything
credential:
  password: anything
```

Reject these in:

- global Dispatch `config.yml`
- `hosts.yml`
- `job.yml`

Allow non-secret references and locations:

```yaml
credential: prod-admin
secret_name: prod-admin-password
vault_uri: https://...
auth: default_azure_credential
target: Dispatch/domain-admin
path: C:\...
username: CONTOSO\prod.admin
```

These are references, locations, auth mode names, or usernames, not secret values.

## Implementation Order

1. Define global Dispatch `config.yml` schema.
2. Define default machine-wide path: `C:\ProgramData\Dispatch\config.yml`.
3. Make Dispatch load that config by default.
4. Implement YAML `--config <path>` as an override.
5. Move credential metadata/catalog into global Dispatch `config.yml`.
6. Stop treating `references.json` as the canonical credential reference catalog.
7. Implement credential metadata lookup from loaded Dispatch config.
8. Implement `prompt` provider.
9. Add `--credential` to `run` and `apply`.
10. Wire resolved credentials into PSRP first. Current implementation covers prompt-provider, DPAPI-file, Windows Credential Manager, and Azure Key Vault runtime resolution for `run ps|cmd|exe --transport psrp`.
11. Add PowerShell wrapper `pscredential` handoff under Roadmap 7: execution wrappers accept `-CredentialName`, accept optional `-Credential <PSCredential>`, prompt with `Get-Credential` when a `pscredential` reference is selected without `-Credential`, delegate `prompt` references to Dispatch's existing runtime prompt, and pass wrapper-supplied credentials through a protected handoff without exposing plaintext.
12. Add `dpapi_file`. Current implementation covers enrollment, restrictive file ACL hardening, test, remove, and PSRP plus raw WinRM runtime resolution.
13. Add `windows_credential_manager`. Current implementation covers enrollment, test, remove, and PSRP plus raw WinRM runtime resolution.
14. Add `azure_keyvault`. Current implementation covers `creds add` / `creds test` reference validation and PSRP plus raw WinRM runtime resolution; creating or updating Key Vault secrets through Dispatch remains out of scope for the first Key Vault slice.

## Final Mental Model

```text
C:\ProgramData\Dispatch\config.yml
  global Dispatch config
  credential definitions
  provider settings
  provider locations
  Key Vault auth modes and secret names

hosts.yml
  host targeting
  optional credential reference only

job.yml
  job execution
  optional credential reference only

CLI --credential
  override only
```

One config. One credential catalog. No passwords in YAML.
