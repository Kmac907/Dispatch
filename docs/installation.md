# Installation

Dispatch installs as both a direct `dispatch` command and a PowerShell module. The primary operator path is the GitHub `irm` installer, which builds the current `win-x64` executable, installs the module, adds the bundled executable folder to PATH, validates both entry points, and removes the temporary source checkout after a successful install.

Primary install:

```powershell
irm https://raw.githubusercontent.com/Kmac907/Dispatch/main/packaging/install-from-source.ps1 | iex
```

Verify both entry points:

```powershell
dispatch --help
dispatch doctor

Import-Module Dispatch -Force
Test-Dispatch
Get-DispatchVersion
```

Lower-level package installation and direct source execution remain available for development, troubleshooting, and CI validation.

## Prerequisites

- Windows 10/11 or Windows Server.
- PowerShell 7 or Windows PowerShell 5.1.
- .NET 8 SDK matching `global.json`. The GitHub `irm` source installer builds Dispatch from source; the .NET runtime alone is not enough.
- Git available on `PATH`.
- Network access to `https://github.com/Kmac907/Dispatch`.
- Administrator shell for `-Scope AllUsers` installation.

## Source Install

The v1 operator path downloads and runs the source installer from GitHub:

```powershell
irm https://raw.githubusercontent.com/Kmac907/Dispatch/main/packaging/install-from-source.ps1 | iex
```

The installer is responsible for:

1. Printing each install phase to the terminal so the operator can see what is happening.
2. Checking prerequisites, including Git when a temporary checkout is needed and the .NET 8 SDK for source builds.
3. Creating a temporary source checkout when it is not run from an existing source tree.
4. Building the self-contained `win-x64` `dispatch.exe`.
5. Assembling the PowerShell module folder.
6. Installing or replacing the module and bundled executable for the selected version.
7. Adding the bundled executable folder to PATH unless `-NoPathUpdate` is supplied or `-DestinationRoot` is used for validation.
8. Validating direct `dispatch --help`, `dispatch version`, module import, and exported wrapper commands.
9. Scheduling an external cleanup helper from the temp folder after validation unless `-NoCleanup` is supplied.

The source installer reports `DispatchPathEntry`, `PathUpdate`, `PathTarget`, `Cleanup`, `CleanupHelperPath`, `CleanupStatusPath`, and `CleanupError` in its final output. Cleanup scheduling failures are reported there without uninstalling the already validated module or converting the installation itself into a failure.

The primary `irm` command is rerunnable. If the same Dispatch module version is already installed, the source installer replaces it, validates the replacement, and only reports success after validation passes.

If the .NET 8 SDK is missing, the source installer fails before cloning/building with a message that explains the SDK requirement. Install the .NET 8 SDK, open a new PowerShell session so PATH is refreshed, and rerun the same install command.

Use script parameters instead of editing the command inline. Current parameters include `-Scope CurrentUser|AllUsers`, `-RepositoryUrl`, `-Ref`, `-SourceRoot`, `-WorkRoot`, `-Configuration`, `-Runtime`, `-DestinationRoot`, `-Force`, `-NoCleanup`, `-NoRestore`, and `-NoPathUpdate`.

## Developer Source Workflow

Use this flow when developing Dispatch or validating a checkout without installing:

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

## Current Package Install

After `.\packaging\build-module.ps1` assembles the package, install it locally:

```powershell
.\packaging\install.ps1 -Scope CurrentUser
```

Use an elevated shell for machine-wide install:

```powershell
.\packaging\install.ps1 -Scope AllUsers
```

Use `-Force` to replace the same installed module version. Use `-ModulePath <path>` when installing an assembled package outside the default `artifacts\module\Dispatch` folder. `-NoPathUpdate` skips PATH changes. `-DestinationRoot <path>` is available for CI or local validation when the real PowerShell module paths and PATH should not be touched.

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

## Verify Source Execution

```powershell
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- --help
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- version
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- doctor
```

Expected result:

- The source-run commands print the canonical CLI command tree, version, and local prerequisite status.
- Installed systems should use `dispatch --help`, `dispatch version`, `dispatch doctor`, `Import-Module Dispatch`, and `Get-Command -Module Dispatch`.

## Upgrade

Run the same source-install command again. It rebuilds from the selected source, replaces the installed module files for the same version, validates the new executable, and fails before reporting success if validation does not pass.

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
