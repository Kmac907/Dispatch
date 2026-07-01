# Installation

Dispatch can currently be built and run from the repository source. Roadmap item `8` now includes local package installation for an already assembled module package; the GitHub `irm` source installer is still planned.

Current local module assembly and install:

```powershell
.\packaging\build-module.ps1
.\packaging\install.ps1 -Scope CurrentUser -Force
Import-Module Dispatch -Force
Get-DispatchVersion
```

This produces an assembled module under `artifacts\module\Dispatch` with the bundled executable at `bin\win-x64\dispatch.exe`, copies it into a versioned `Dispatch\<version>` folder under the selected PowerShell module root, and validates the installed manifest, bundled executable, module import, exported commands, and `Get-DispatchVersion`.

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

For the current local package installer, run `.\packaging\build-module.ps1` and then `.\packaging\install.ps1 -Scope CurrentUser -Force` again. After the source installer is implemented, run the same source-install command again; it should rebuild from the current GitHub source, replace the installed module files, validate the new executable, and leave the previous installation untouched only if validation fails.

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
