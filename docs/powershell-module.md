# PowerShell Module

The PowerShell module is a wrapper over the bundled `dispatch.exe`. The direct `dispatch` command remains the canonical engine and command surface.

Status: partial Roadmap `7`.

## Commands

Implemented wrapper commands:

```powershell
Test-Dispatch
Get-DispatchVersion
Invoke-DispatchPowerShell
```

Planned remaining execution wrapper commands:

```powershell
Invoke-DispatchCommand
Invoke-DispatchExecutable
Invoke-DispatchJob
```

## Examples

```powershell
Import-Module Dispatch
Test-Dispatch
Get-DispatchVersion
Invoke-DispatchPowerShell -Script .\Fix.ps1 -Target PC001 -Transport psrp -Plan
```

When the module is installed by the future packaging slice, it resolves the bundled `bin\win-x64\dispatch.exe`. In developer or test checkouts, use `-DispatchPath <path>` or set `DISPATCH_EXE` to point at the CLI executable.

Current PowerShell execution wrapper:

```powershell
Invoke-DispatchPowerShell -Script .\Fix.ps1 -Target PC001 -Transport psrp
Invoke-DispatchPowerShell -Script .\Fix.ps1 -Target PC001 -Transport psrp -CredentialName admin-session
```

Planned remaining execution wrapper examples:

```powershell
Invoke-DispatchCommand -Command "whoami" -Target PC001 -Transport winrm
Invoke-DispatchJob -Job .\job.yml
```

## Automation Rule

Wrapper commands rely on structured output or result files. They do not parse rich terminal rendering. `Test-Dispatch` calls `dispatch.exe doctor --output json` and returns the parsed diagnostics object with `ExitCode` and `DispatchPath` attached. `Get-DispatchVersion` calls `dispatch.exe version` and returns a small object with the product, version, command service, resolved dispatch path, and raw output. `Invoke-DispatchPowerShell` calls `dispatch.exe run ps <script.ps1> --output json --no-progress`, returns the parsed run result with `ExitCode` and `DispatchPath` attached, and passes `-CredentialName <name>` through to the existing CLI `--credential <name>` endpoint credential reference.

## PSCredential Handoff

`provider: pscredential` is only valid through the PowerShell module wrapper.

`Invoke-DispatchPowerShell` currently accepts `-CredentialName <name>` and passes it through to Dispatch as `--credential <name>`. Planned remaining execution wrappers will do the same. For the later `provider: pscredential` handoff slice, `-Credential <PSCredential>` is optional.

Current `Invoke-DispatchPowerShell` does not yet accept `-Credential <PSCredential>`. When the later protected handoff slice lands, supplied credentials will look like this:

```powershell
$cred = Get-Credential CONTOSO\AdminUser

Invoke-DispatchPowerShell `
  -Script .\Fix.ps1 `
  -Target PC001 `
  -CredentialName admin-session `
  -Credential $cred
```

When that later handoff slice lands and `-Credential` is omitted for a `pscredential` reference, the module will prompt with `Get-Credential`, using the configured username when present:

```powershell
Invoke-DispatchPowerShell `
  -Script .\Fix.ps1 `
  -Target PC001 `
  -CredentialName admin-session
```

That future wrapper handoff must pass the credential to Dispatch without putting the password on a command line or writing it to config, job files, inventory files, logs, traces, results, or artifacts. Direct `dispatch.exe` must reject `pscredential` unless a compatible protected wrapper handoff is present.

`provider: prompt` remains different: Dispatch owns that secure runtime prompt. When the module selects a `prompt` reference, the module should not call `Get-Credential`; it should let Dispatch prompt normally.

## No Separate Shell

The module does not introduce an alternate interactive shell launcher. It maps PowerShell-friendly functions onto the documented Dispatch command tree.
