# PowerShell Module

The PowerShell module is a wrapper over the bundled `dispatch.exe`. The direct `dispatch` command remains the canonical engine and command surface.

Status: partial Roadmap `7`.

## Commands

Implemented wrapper commands:

```powershell
Test-Dispatch
Get-DispatchVersion
Invoke-DispatchPowerShell
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

When the module is assembled with `packaging/build-module.ps1`, it resolves the bundled `bin\win-x64\dispatch.exe`. In developer or test checkouts without an assembled module package, use `-DispatchPath <path>` or set `DISPATCH_EXE` to point at the CLI executable.

Current PowerShell execution wrapper:

```powershell
Invoke-DispatchPowerShell -Script .\Fix.ps1 -Target PC001 -Transport psrp
Invoke-DispatchPowerShell -Script .\Fix.ps1 -Target PC001 -Transport psrp -CredentialName admin-session
Invoke-DispatchCommand -Command "whoami" -Target PC001 -Transport winrm
Invoke-DispatchExecutable -Path C:\Windows\System32\hostname.exe -Target PC001 -Transport psrp
Invoke-DispatchJob -JobPath .\job.yml -Target PC001 -Plan
```

## Automation Rule

Wrapper commands rely on structured output or result files. They do not parse rich terminal rendering. `Test-Dispatch` calls `dispatch.exe doctor --output json` and returns the parsed diagnostics object with `ExitCode` and `DispatchPath` attached. `Get-DispatchVersion` calls `dispatch.exe version` and returns a small object with the product, version, command service, resolved dispatch path, and raw output. `Invoke-DispatchPowerShell`, `Invoke-DispatchCommand`, and `Invoke-DispatchExecutable` call `dispatch.exe run ps|cmd|exe ... --output json --no-progress`, return the parsed run result with `ExitCode` and `DispatchPath` attached, and pass `-CredentialName <name>` through to the existing CLI `--credential <name>` endpoint credential reference. `Invoke-DispatchJob` calls `dispatch.exe apply <job.yml> --output json --no-progress`, returns the parsed apply result with `ExitCode` and `DispatchPath` attached, and maps job options such as `-Target`, `-Inventory`, `-CredentialName`, `-Transport`, `-Tags`, `-SkipTags`, `-Serial`, `-Plan`, `-Check`, and `-Diff` onto the matching CLI options.

## PSCredential Handoff

`provider: pscredential` is only valid through the PowerShell module wrapper.

The current execution wrappers accept `-CredentialName <name>` and pass it through to Dispatch as `--credential <name>`. For the later `provider: pscredential` handoff slice, `-Credential <PSCredential>` is optional.

Current execution wrappers do not yet accept `-Credential <PSCredential>`. When the later protected handoff slice lands, supplied credentials will look like this:

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
