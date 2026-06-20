# Credential Store Plan

This document is the canonical credential store and credential resolution design for Dispatch. It applies to the direct `dispatch.exe` command surface and to the planned PowerShell module wrapper.

## Scope Distinction

This plan covers endpoint credentials selected by credential references such as `credential: prod-admin` or CLI `--credential prod-admin`.

It does not make Dispatch responsible for general runtime script secret handoff, SAS token delivery, Blob payload retrieval, or Azure Key Vault payload-secret retrieval. Those remain separate roadmap concerns unless an explicit later slice adds them.

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
    username: SCF\prod.admin

  helpdesk-local:
    provider: dpapi_file
    username: .\helpdesk-admin
    path: C:\ProgramData\Dispatch\Credentials\helpdesk-local.cred

  domain-admin:
    provider: windows_credential_manager
    username: SCF\domain.admin
    target: Dispatch/domain-admin

  kv-prod-admin:
    provider: azure_keyvault
    username: SCF\prod.admin
    vault_uri: https://scf-dispatch-kv.vault.azure.net/
    secret_name: prod-admin-password
    auth: default_azure_credential
```

This file is the credential reference catalog and provider-location catalog.

Meaning:

```text
prod-admin       -> prompt user for SCF\prod.admin password
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
    username: SCF\prod.admin
    vault_uri: https://scf-dispatch-kv.vault.azure.net/
    secret_name: prod-admin-password
    auth: default_azure_credential
```

Meaning:

```text
Use Azure SDK DefaultAzureCredential to authenticate to Key Vault.
Then read secret prod-admin-password.
Use that secret as the password for SCF\prod.admin.
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
    username: SCF\prod.admin
    vault_uri: https://scf-dispatch-kv.vault.azure.net/
    secret_name: prod-admin-password
    auth: managed_identity
```

User-assigned managed identity:

```yaml
credentials:
  kv-prod-admin:
    provider: azure_keyvault
    username: SCF\prod.admin
    vault_uri: https://scf-dispatch-kv.vault.azure.net/
    secret_name: prod-admin-password
    auth: managed_identity
    managed_identity_client_id: 00000000-0000-0000-0000-000000000000
```

Azure CLI explicit mode:

```yaml
credentials:
  kv-prod-admin:
    provider: azure_keyvault
    username: SCF\prod.admin
    vault_uri: https://scf-dispatch-kv.vault.azure.net/
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
    username: SCF\prod.admin
```

Runtime:

```powershell
dispatch apply .\job.yml
```

Prompt:

```text
Credential: prod-admin
Username: SCF\prod.admin
Password:
```

Password is kept in memory only and discarded after the run.

Current implementation:

- Implemented for real `dispatch run ps|cmd|exe ... --transport psrp` execution.
- The prompt happens after planning and before live rendering or endpoint work starts.
- `--plan` / dry-run paths validate and carry the reference name but do not prompt.
- The resolved password is converted to an in-memory PSRP `PSCredential` for command, script, and artifact sessions.
- Raw WinRM, PsExec, YAML `apply`, and non-prompt providers remain later slices.

## PSCredential Provider

PowerShell module wrapper only.

Config:

```yaml
credentials:
  prod-admin:
    provider: pscredential
    username: SCF\prod.admin
```

PowerShell:

```powershell
$cred = Get-Credential SCF\prod.admin

Invoke-DispatchRun `
  -Job .\job.yml `
  -CredentialName prod-admin `
  -Credential $cred
```

Rules:

- Only valid through the PowerShell wrapper.
- Direct `dispatch.exe` must reject `provider: pscredential` unless a compatible protected handoff is present.
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

## Windows Credential Manager Provider

Config:

```yaml
credentials:
  domain-admin:
    provider: windows_credential_manager
    username: SCF\domain.admin
    target: Dispatch/domain-admin
```

Enrollment:

```powershell
dispatch creds add domain-admin
```

Dispatch securely prompts and stores the credential in Windows Credential Manager using the configured target.

## Azure Key Vault Provider

Config:

```yaml
credentials:
  kv-prod-admin:
    provider: azure_keyvault
    username: SCF\prod.admin
    vault_uri: https://scf-dispatch-kv.vault.azure.net/
    secret_name: prod-admin-password
    auth: default_azure_credential
```

Run:

```powershell
dispatch apply .\job.yml
```

Dispatch retrieves the secret from:

```text
https://scf-dispatch-kv.vault.azure.net/secrets/prod-admin-password
```

Then builds an in-memory credential object for the transport.

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
    username: SCF\prod.admin
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
    username: SCF\prod.admin
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
    username: SCF\domain.admin
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
    username: SCF\prod.admin
    vault_uri: https://scf-dispatch-kv.vault.azure.net/
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

windows_credential_manager:
  add = prompt and store in Windows Credential Manager

azure_keyvault:
  add = validate Azure secret reference, no local storage
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
username: SCF\prod.admin
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
10. Wire resolved credentials into PSRP first. Current implementation covers prompt-provider runtime resolution for `run ps|cmd|exe --transport psrp`.
11. Add PowerShell wrapper `pscredential` handoff.
12. Add `dpapi_file`.
13. Add `windows_credential_manager`.
14. Add `azure_keyvault`.

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
