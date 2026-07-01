# Distribution

Distribution describes how operators will install Dispatch once the v1 package surfaces are implemented.

Status: partial Roadmap `8`.

Current support: `packaging/build-module.ps1` assembles a local PowerShell module folder with a bundled self-contained `win-x64` `dispatch.exe` at `bin\win-x64\dispatch.exe`, validates the module manifest, imports the assembled module, and verifies `Get-DispatchVersion` through the bundled executable. `install-from-source.ps1`, `install.ps1`, and ZIP packaging are not implemented yet.

## Module Assembly

Build the current module package from an existing checkout:

```powershell
.\packaging\build-module.ps1
```

The default output is:

```text
artifacts\module\Dispatch\
  Dispatch.psd1
  Dispatch.psm1
  bin\win-x64\dispatch.exe
```

Use `-Configuration Debug|Release`, `-Runtime win-x64`, `-OutputPath <path>`, and `-NoRestore` when needed for local validation or CI.

## Source Install

Planned primary v1 flow:

```powershell
irm https://raw.githubusercontent.com/Kmac907/Dispatch/main/packaging/install-from-source.ps1 | iex
```

The installer should:

1. Create a temporary checkout.
2. Build the self-contained `win-x64` executable.
3. Assemble the PowerShell module.
4. Install the module and bundled executable.
5. Validate `dispatch --help`, `dispatch version`, module import, and exported commands.
6. Clean up temporary source files after successful validation.

## Existing Checkout

Planned developer/troubleshooting mode:

```powershell
.\packaging\install-from-source.ps1 -NoCleanup
```

## Packaged Install

Planned v1 local package install from an already assembled package:

```powershell
.\packaging\install.ps1 -Scope CurrentUser
.\packaging\install.ps1 -Scope AllUsers
```

## Optional ZIP

An optional release convenience artifact may be produced:

```text
Dispatch-<version>-win-x64.zip
```

## Not V1

- MSI installer.
- WinGet package.
- Azure Artifacts feed.
- Public PowerShell Gallery publishing.
