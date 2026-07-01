# Installation

Dispatch can be installed from source, installed from an assembled module package, installed from the optional ZIP package, or run directly from the repository source during development.

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

The source installer builds the self-contained executable, assembles the module under `artifacts\module\Dispatch`, copies it into a versioned `Dispatch\<version>` folder under the selected PowerShell module root, validates the installed manifest, bundled executable, module import, exported commands, `Get-DispatchVersion`, and `dispatch --help`, and reports cleanup status for temporary source checkouts.

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

Older bootstrap links can use the compatibility wrapper:

```powershell
irm https://raw.githubusercontent.com/Kmac907/Dispatch/main/packaging/bootstrap-install.ps1 | iex
```

The bootstrap wrapper is not a separate installer. It delegates to `install-from-source.ps1` from a checkout when the files are side by side, or downloads the canonical source installer from the selected GitHub repository and branch/ref before invoking it.

The installer is responsible for:

1. Creating a temporary source checkout when it is not run from an existing source tree.
2. Building the self-contained `win-x64` `dispatch.exe`.
3. Assembling the PowerShell module folder.
4. Installing the module and bundled executable.
5. Validating `dispatch --help`, `dispatch version`, module import, and exported wrapper commands.
6. Scheduling an external cleanup helper from the temp folder after validation unless `-NoCleanup` is supplied.

The source installer reports `Cleanup`, `CleanupHelperPath`, `CleanupStatusPath`, and `CleanupError` in its final output. Cleanup scheduling failures are reported there without uninstalling the already validated module or converting the installation itself into a failure.

Use script parameters instead of editing the command inline. Current parameters include `-Scope CurrentUser|AllUsers`, `-RepositoryUrl`, `-Ref`, `-SourceRoot`, `-WorkRoot`, `-Configuration`, `-Runtime`, `-DestinationRoot`, `-Force`, `-NoCleanup`, and `-NoRestore`. The compatibility bootstrap wrapper accepts the same parameters and forwards them to `install-from-source.ps1`.

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

## ZIP Package Install

Create the optional ZIP from a checkout:

```powershell
.\packaging\build-module.ps1 -CreateZip
```

The generated file is written under `artifacts\packages\` as `Dispatch-<version>-win-x64.zip`. Extract it and run the included installer from the extracted `Dispatch\` package root:

```powershell
Expand-Archive .\artifacts\packages\Dispatch-0.1.0-win-x64.zip -DestinationPath .\artifacts\zip-install -Force
.\artifacts\zip-install\Dispatch\install.ps1 -Scope CurrentUser -Force
```

The ZIP contains only the installable package root: `Dispatch.psd1`, `Dispatch.psm1`, `install.ps1`, and `bin\win-x64\dispatch.exe`. It does not contain source files, tests, workflow files, `.git`, `.codex`, or publish intermediates.

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
