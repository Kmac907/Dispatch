# Installation

Dispatch can currently be built and run from the repository source. Roadmap item `8` now includes source installation and local package installation for an already assembled module package.

Current local module assembly and install:

```powershell
.\packaging\install-from-source.ps1 -Scope CurrentUser -Force
```

To run the lower-level package assembly and install steps separately:

```powershell
.\packaging\build-module.ps1
.\packaging\install.ps1 -Scope CurrentUser -Force
Import-Module Dispatch -Force
Get-DispatchVersion
```

The source installer builds the self-contained executable, assembles the module under `artifacts\module\Dispatch`, copies it into a versioned `Dispatch\<version>` folder under the selected PowerShell module root, and validates the installed manifest, bundled executable, module import, exported commands, `Get-DispatchVersion`, and `dispatch --help`.

## Prerequisites

- Windows 10/11 or Windows Server.
- PowerShell 7 or Windows PowerShell 5.1.
- .NET SDK matching `global.json`.
- Git available on `PATH`.
- Network access to `https://github.com/Kmac907/Dispatch`.
- Administrator shell for `-Scope AllUsers` installation.

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

## Source Install

The v1 operator path downloads and runs the source installer from GitHub:

```powershell
irm https://raw.githubusercontent.com/Kmac907/Dispatch/main/packaging/install-from-source.ps1 | iex
```

The installer is responsible for:

1. Creating a temporary source checkout when it is not run from an existing source tree.
2. Building the self-contained `win-x64` `dispatch.exe`.
3. Assembling the PowerShell module folder.
4. Installing the module and bundled executable.
5. Validating `dispatch --help`, `dispatch version`, module import, and exported wrapper commands.
6. Scheduling cleanup of the temporary source checkout after validation unless `-NoCleanup` is supplied.

Use script parameters instead of editing the command inline. Current parameters include `-Scope CurrentUser|AllUsers`, `-RepositoryUrl`, `-Ref`, `-SourceRoot`, `-WorkRoot`, `-Configuration`, `-Runtime`, `-DestinationRoot`, `-Force`, `-NoCleanup`, and `-NoRestore`.

## Current Package Install

After `.\packaging\build-module.ps1` assembles the package, install it locally:

```powershell
.\packaging\install.ps1 -Scope CurrentUser
```

Use an elevated shell for machine-wide install:

```powershell
.\packaging\install.ps1 -Scope AllUsers
```

Use `-Force` to replace the same installed module version. Use `-ModulePath <path>` when installing an assembled package outside the default `artifacts\module\Dispatch` folder. `-DestinationRoot <path>` is available for CI or local validation when the real PowerShell module paths should not be touched.

Security notes:

- Review the installer source before running an `irm | iex` command in controlled environments.
- Use an elevated shell only when installing for all users.
- The installer should not write endpoint passwords or credential secrets to the command line, logs, or repository checkout.

## Developer Install From Existing Checkout

This mode builds and installs from an existing checkout while preserving the source tree:

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

Run the same source-install command again with `-Force`. It rebuilds from the selected source, replaces the installed module files for the same version, validates the new executable, and fails before reporting success if validation does not pass.

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
