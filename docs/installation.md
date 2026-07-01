# Installation

Dispatch can currently be built and run from the repository source. The v1 source installer and package installer are planned under roadmap item `8` and are not implemented yet.

Roadmap `7` includes current local module assembly:

```powershell
.\packaging\build-module.ps1
Import-Module .\artifacts\module\Dispatch\Dispatch.psd1 -Force
Get-DispatchVersion
```

This produces an assembled module under `artifacts\module\Dispatch` with the bundled executable at `bin\win-x64\dispatch.exe`. It does not install the module into a PowerShell module path; Roadmap `8` owns installation.

## Prerequisites

- Windows 10/11 or Windows Server.
- PowerShell 7 or Windows PowerShell 5.1.
- .NET SDK matching `global.json`.
- Git available on `PATH`.
- Network access to `https://github.com/Kmac907/Dispatch`.
- Administrator shell for future all-users installation.

## Current Source Workflow

Use this flow today:

```powershell
git clone https://github.com/Kmac907/Dispatch.git
cd Dispatch
dotnet build .\Dispatch.sln
dotnet test .\Dispatch.sln
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- --help
```

Run a plan from source:

```powershell
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- run ps .\Fix.ps1 --target PC001 --transport psrp --plan
```

## Planned Source Install

The planned v1 operator path will download and run the source installer from GitHub:

```powershell
irm https://raw.githubusercontent.com/Kmac907/Dispatch/main/packaging/install-from-source.ps1 | iex
```

This command is not available until `packaging/install-from-source.ps1` is implemented.

The installer is responsible for:

1. Creating a temporary source checkout.
2. Building the self-contained `win-x64` `dispatch.exe`.
3. Assembling the PowerShell module folder.
4. Installing the module and bundled executable.
5. Validating `dispatch --help`, `dispatch version`, module import, and exported wrapper commands.
6. Cleaning up the temporary source checkout after validation.

Use script parameters instead of editing the command inline once the installer exposes install scope and cleanup options.

Security notes:

- Review the installer source before running an `irm | iex` command in controlled environments.
- Use an elevated shell only when installing for all users.
- The installer should not write endpoint passwords or credential secrets to the command line, logs, or repository checkout.

## Planned Developer Install From Existing Checkout

After `packaging/install-from-source.ps1` exists, this mode will build and install from an existing checkout while preserving the source tree:

```powershell
git clone https://github.com/Kmac907/Dispatch.git
cd Dispatch
.\packaging\install-from-source.ps1 -NoCleanup
```

## Verify Installation

```powershell
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- --help
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- version
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- doctor
```

Expected result:

- The source-run commands print the canonical CLI command tree, version, and local prerequisite status.
- After the module wrapper and installer are implemented, `Import-Module Dispatch` and `Get-Command -Module Dispatch` should validate the installed wrapper commands.

## Upgrade

After the source installer is implemented, run the same source-install command again. The installer should rebuild from the current GitHub source, replace the installed module files, validate the new executable, and leave the previous installation untouched only if validation fails.

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

Do not remove `C:\ProgramData\Dispatch\Runs\` if you still need prior run evidence. Do not remove `C:\ProgramData\Dispatch\Credentials\` unless the local DPAPI file credentials are no longer needed or have been re-enrolled elsewhere.
