# PowerShell Module

The PowerShell module is a wrapper over the bundled `dispatch.exe`. The direct `dispatch` command remains the canonical engine and command surface.

Status: planned v1.

## Commands

Planned wrapper commands:

```powershell
Invoke-DispatchPowerShell
Invoke-DispatchCommand
Invoke-DispatchExecutable
Invoke-DispatchJob
Test-Dispatch
Get-DispatchVersion
```

## Examples

```powershell
Import-Module Dispatch
Invoke-DispatchPowerShell -Script .\Fix.ps1 -Target PC001 -Transport psrp
Invoke-DispatchCommand -Command "whoami" -Target PC001 -Transport winrm
Invoke-DispatchJob -Job .\job.yml
Test-Dispatch
Get-DispatchVersion
```

## Automation Rule

Wrapper commands should rely on structured output or result files. They should not parse rich terminal rendering.

## PSCredential Handoff

`provider: pscredential` is only valid through the PowerShell module wrapper.

Planned execution wrappers accept `-CredentialName <name>` to select a configured credential reference. For `provider: pscredential`, `-Credential <PSCredential>` is optional.

If `-Credential` is supplied, the module uses that live object:

```powershell
$cred = Get-Credential CONTOSO\AdminUser

Invoke-DispatchPowerShell `
  -Script .\Fix.ps1 `
  -Target PC001 `
  -CredentialName admin-session `
  -Credential $cred
```

If `-Credential` is omitted, the module prompts with `Get-Credential`, using the configured username when present:

```powershell
Invoke-DispatchPowerShell `
  -Script .\Fix.ps1 `
  -Target PC001 `
  -CredentialName admin-session
```

The wrapper must hand the credential to Dispatch without putting the password on a command line or writing it to config, job files, inventory files, logs, traces, results, or artifacts. Direct `dispatch.exe` must reject `pscredential` unless a compatible protected wrapper handoff is present.

`provider: prompt` remains different: Dispatch owns that secure runtime prompt. When the module selects a `prompt` reference, the module should not call `Get-Credential`; it should let Dispatch prompt normally.

## No Separate Shell

The module does not introduce an alternate interactive shell launcher. It maps PowerShell-friendly functions onto the documented Dispatch command tree.
