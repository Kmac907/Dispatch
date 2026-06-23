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

The wrapper can accept a `PSCredential` in-process and hand it to Dispatch without putting the password on a command line. Direct `dispatch.exe` must reject `pscredential` unless a compatible protected handoff is present.

## No Separate Shell

The module does not introduce an alternate interactive shell launcher. It maps PowerShell-friendly functions onto the documented Dispatch command tree.
