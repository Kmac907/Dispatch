# PowerShell Module

The PowerShell module is a wrapper over the bundled `dispatch.exe`. The direct `dispatch` command remains the canonical engine and command surface.

Status: Roadmap `7` complete.

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
dispatch --help
dispatch run ps .\Fix.ps1 --target PC001 --transport psrp

Test-Dispatch
Get-DispatchVersion
Invoke-DispatchPowerShell -Script .\Fix.ps1 -Target PC001 -Transport psrp -Plan
```

Normal installation adds the bundled executable folder to PATH, so operators can call `dispatch` directly for the full CLI. PowerShell normally auto-loads the installed `Dispatch` module when a wrapper command such as `Test-Dispatch` or `Invoke-DispatchPowerShell` is called. Use `Import-Module Dispatch -Force` only when auto-loading is disabled or a shell needs to reload the module after an update. When the module is assembled with `packaging/build-module.ps1`, it resolves the bundled `bin\win-x64\dispatch.exe`. In developer or test checkouts without an assembled module package, use `-DispatchPath <path>` or set `DISPATCH_EXE` to point at the CLI executable.

Current PowerShell execution wrapper:

```powershell
Invoke-DispatchPowerShell -Script .\Fix.ps1 -Target PC001 -Transport psrp
Invoke-DispatchPowerShell -Script .\Fix.ps1 -Target PC001 -Transport psrp -CredentialName admin-session
Invoke-DispatchCommand -Command "whoami" -Target PC001 -Transport winrm
Invoke-DispatchExecutable -Path C:\Windows\System32\hostname.exe -Target PC001 -Transport psrp
Invoke-DispatchJob -JobPath .\job.yml -Target PC001 -Plan
```

## Automation Rule

Wrapper commands rely on structured output or result files. They do not parse rich terminal rendering. `Test-Dispatch` calls `dispatch.exe doctor --output json` and returns the parsed diagnostics object with `ExitCode` and `DispatchPath` attached. `Get-DispatchVersion` calls `dispatch.exe version` and returns a small object with the product, version, command service, resolved dispatch path, and raw output. `Invoke-DispatchPowerShell`, `Invoke-DispatchCommand`, and `Invoke-DispatchExecutable` call `dispatch.exe run ps|cmd|exe ... --output json --no-progress`, return the parsed run result with `ExitCode` and `DispatchPath` attached, and pass `-CredentialName <name>` through to the existing CLI `--credential <name>` endpoint credential reference. `Invoke-DispatchJob` calls `dispatch.exe apply <job.yml> --output json --no-progress`, returns the parsed apply result with `ExitCode` and `DispatchPath` attached, and maps job options such as `-Target`, `-Inventory`, `-CredentialName`, `-Credential`, `-Transport`, `-Tags`, `-SkipTags`, `-Serial`, `-Plan`, `-Check`, and `-Diff` onto the matching CLI options.

## PSCredential Handoff

`provider: pscredential` is only valid through the PowerShell module wrapper. Direct `dispatch.exe --credential <name>` rejects `pscredential` unless a compatible protected wrapper handoff is present.

The execution wrappers accept `-CredentialName <name>` and pass it through to Dispatch as `--credential <name>`. When that configured reference uses `provider: pscredential`, `-Credential <PSCredential>` is optional. If supplied, the module uses that live credential object:

```powershell
$cred = Get-Credential CONTOSO\AdminUser

Invoke-DispatchPowerShell `
  -Script .\Fix.ps1 `
  -Target PC001 `
  -CredentialName admin-session `
  -Credential $cred
```

When `-Credential` is omitted for a `pscredential` reference, the module prompts with `Get-Credential`, using the configured username when present:

```powershell
Invoke-DispatchPowerShell `
  -Script .\Fix.ps1 `
  -Target PC001 `
  -CredentialName admin-session
```

The wrapper creates a short-lived DPAPI CurrentUser-protected handoff file, passes only a local handoff path to `dispatch.exe` through process environment, and deletes the handoff file after Dispatch consumes it. The password is not placed on a command line and is not written to config, job files, inventory files, logs, traces, results, or artifacts. If the supplied credential username does not match the configured username, the wrapper fails before launching Dispatch.

`provider: prompt` remains different: Dispatch owns that secure runtime prompt. When the module selects a `prompt` reference, the module does not call `Get-Credential`; it lets Dispatch prompt normally.

## No Separate Shell

The module does not introduce an alternate interactive shell launcher. It maps PowerShell-friendly functions onto the documented Dispatch command tree.
