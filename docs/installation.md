# Installation

Dispatch v1 installs from source and packages the resulting `dispatch.exe` with the PowerShell module wrapper.

## Prerequisites

- Windows 10/11 or Windows Server.
- PowerShell 7 or Windows PowerShell 5.1.
- .NET SDK matching `global.json`.
- Git available on `PATH`.
- Network access to `https://github.com/Kmac907/Dispatch`.
- Administrator shell for all-users installation.

## Source Install

The primary v1 operator path downloads and runs the source installer from GitHub:

```powershell
irm https://raw.githubusercontent.com/Kmac907/Dispatch/main/packaging/install-from-source.ps1 | iex
```

The installer is responsible for:

1. Creating a temporary source checkout.
2. Building the self-contained `win-x64` `dispatch.exe`.
3. Assembling the PowerShell module folder.
4. Installing the module and bundled executable.
5. Validating `dispatch --help`, `dispatch version`, module import, and exported wrapper commands.
6. Cleaning up the temporary source checkout after validation.

Use script parameters instead of editing the command inline once the installer exposes install scope and cleanup options.

## Developer Install From Existing Checkout

Use this when you intentionally want to keep the source tree:

```powershell
git clone https://github.com/Kmac907/Dispatch.git
cd Dispatch
.\packaging\install-from-source.ps1 -NoCleanup
```

## Verify Installation

```powershell
dispatch --help
dispatch version
dispatch doctor
Import-Module Dispatch
Get-Command -Module Dispatch
```

## Upgrade

Run the same source-install command again. The installer should rebuild from the current GitHub source, replace the installed module files, validate the new executable, and leave the previous installation untouched only if validation fails.

## Uninstall

Remove the installed Dispatch module folder from the selected PowerShell module path and remove any machine-wide config or run history only when that data is no longer needed:

```powershell
Remove-Module Dispatch -ErrorAction SilentlyContinue
```

Default data locations:

```text
C:\ProgramData\Dispatch\config.yml
C:\ProgramData\Dispatch\Runs\
C:\ProgramData\Dispatch\Credentials\
```
