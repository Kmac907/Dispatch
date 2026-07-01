# Distribution

Distribution describes how operators will install Dispatch once the v1 package surfaces are implemented.

Status: Roadmap `8` local/source/ZIP packaging implemented. Enterprise distribution remains Roadmap `13`.

Current support: `packaging/build-module.ps1` assembles a local PowerShell module folder with a bundled self-contained `win-x64` `dispatch.exe` at `bin\win-x64\dispatch.exe`, copies the package installer into the module root, validates the module manifest, imports the assembled module, verifies `Get-DispatchVersion` through the bundled executable, and can create a validated release ZIP with `-CreateZip`.

`packaging/install.ps1` installs an already assembled package into a `CurrentUser` or `AllUsers` PowerShell module scope and validates the installed manifest, bundled executable, module import, exported commands, and `Get-DispatchVersion`.

`packaging/install-from-source.ps1` builds and installs from an existing checkout or clones the GitHub repository for the `irm | iex` flow, then validates the installed module, bundled executable, exported commands, and `dispatch --help`. After successful validation from a temporary checkout, it schedules an external cleanup helper from the temp folder and reports cleanup status without treating cleanup scheduling failure as an installation failure.

`packaging/bootstrap-install.ps1` is a compatibility wrapper for older bootstrap links. It delegates to `install-from-source.ps1` from a checkout when available, or downloads the canonical source installer from the configured GitHub repository and branch/ref before delegating.

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
  install.ps1
  bin\win-x64\dispatch.exe
```

Use `-Configuration Debug|Release`, `-Runtime win-x64`, `-OutputPath <path>`, and `-NoRestore` when needed for local validation or CI.

## Source Install

Primary v1 flow:

```powershell
irm https://raw.githubusercontent.com/Kmac907/Dispatch/main/packaging/install-from-source.ps1 | iex
```

Compatibility bootstrap flow:

```powershell
irm https://raw.githubusercontent.com/Kmac907/Dispatch/main/packaging/bootstrap-install.ps1 | iex
```

The installer:

1. Creates a temporary checkout when it is not run from an existing source tree.
2. Build the self-contained `win-x64` executable.
3. Assemble the PowerShell module.
4. Install the module and bundled executable.
5. Validate `dispatch --help`, `dispatch version`, module import, and exported commands.
6. Schedules an external cleanup helper from the temp folder after successful validation unless `-NoCleanup` is supplied.

The installer returns `Cleanup`, `CleanupHelperPath`, `CleanupStatusPath`, and `CleanupError` fields. A cleanup scheduling failure is reported in those fields, but it does not remove the already validated module or turn the successful install into a failed install.

## Existing Checkout

Developer/troubleshooting mode:

```powershell
.\packaging\install-from-source.ps1 -NoCleanup
```

Use `-SourceRoot <path>` to build from a specific checkout, `-DestinationRoot <path>` for CI/local validation without touching real module paths, `-Force` to replace the same installed module version, and `-NoRestore` when dependencies are already restored.

When run from a checkout, `bootstrap-install.ps1` uses the sibling `install-from-source.ps1`. When run through `irm`, it downloads `install-from-source.ps1` from the same `-RepositoryUrl` and `-Ref`, invokes it with the same parameters, and removes the temporary installer copy afterward.

## Packaged Install

Install an already assembled package:

```powershell
.\packaging\install.ps1 -Scope CurrentUser
.\packaging\install.ps1 -Scope AllUsers
```

`CurrentUser` installs into the current shell family's per-user module path. `AllUsers` installs into the current shell family's machine module path and requires an elevated shell. Use `-ModulePath <path>` to install a package outside the default `artifacts\module\Dispatch` location, `-Force` to replace the same installed module version, and `-DestinationRoot <path>` only for CI or local validation where the real PowerShell module paths should not be touched.

## Optional ZIP

Create the optional release convenience artifact from the same build script:

```powershell
.\packaging\build-module.ps1 -CreateZip
```

The ZIP is written under `artifacts\packages\` with the module manifest version and runtime in the file name, for example:

```text
artifacts\packages\Dispatch-0.1.0-win-x64.zip
```

The ZIP has one installable root:

```text
Dispatch\
  Dispatch.psd1
  Dispatch.psm1
  install.ps1
  bin\win-x64\dispatch.exe
```

The build script creates this package from a clean staging folder. Source files, tests, `.git`, `.codex`, workflow files, intermediate `bin` / `obj`, and publish-only output are not included.

When `-CreateZip` is used, the script extracts the ZIP to a temporary folder, runs the extracted `Dispatch\install.ps1` into a temporary module root, imports the installed module, verifies exported commands and `Get-DispatchVersion`, and checks the bundled `dispatch.exe --help` before returning success.

Use `-PackageOutputPath <path>` to place the ZIP somewhere other than `artifacts\packages`. CI/CD can upload the generated ZIP to GitHub Releases, but release upload remains outside the local build script.

## Not V1

- MSI installer.
- WinGet package.
- Azure Artifacts feed.
- Public PowerShell Gallery publishing.
