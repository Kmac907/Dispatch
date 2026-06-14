# Dispatch

Dispatch is a Windows-native, push-style remote script execution tool for endpoint administrators. It is inspired by the operational idea of Ansible, but v1 is intentionally narrower: Dispatch is a script orchestrator and executor, not a playbook engine, package manager, configuration-management platform, or endpoint agent.

The v1 goal is to let an administrator run a PowerShell script on one or more Windows endpoints through PsExec, collect logs and structured results, and expose the workflow through both `dispatch.exe` and a PowerShell module. Post-MVP transports will add separate Ansible-style `psrp` and raw `winrm` protocol integrations for script and command execution.

V1 intentionally does not implement command execution, PSRP/raw WinRM transports, or Dispatch-managed credential/SAS handoff. Those surfaces are modeled so the architecture stays consistent, but they are post-MVP.

## V1 Distribution

The primary v1 distribution model is source install from a private Azure DevOps repository.

```powershell
git clone https://dev.azure.com/<org>/<project>/_git/Dispatch
cd Dispatch
.\packaging\bootstrap-install.ps1
Import-Module Dispatch
Start-Dispatch
```

`bootstrap-install.ps1` will build `dispatch.exe`, assemble the PowerShell module, install it into the selected module scope, validate that the module and bundled executable work, then remove the cloned source tree including the bootstrap script itself. After a successful bootstrap install, the installed PowerShell module and bundled `dispatch.exe` should remain.

`install-from-source.ps1` is the reusable build/install helper for developers and CI jobs that intentionally keep the source checkout.

## Long-Term Distribution

The long-term enterprise distribution model is an Azure Artifacts PowerShell repository backed by a NuGet feed.

```powershell
Register-PSResourceRepository `
  -Name DispatchInternal `
  -Uri "https://pkgs.dev.azure.com/<org>/<project>/_packaging/<feed>/nuget/v3/index.json" `
  -Trusted

Install-PSResource Dispatch -Repository DispatchInternal -Scope CurrentUser
Import-Module Dispatch
Start-Dispatch
```

## Project Status

Dispatch has an initial .NET foundation, dry-run request planning, deterministic target resolution, local run layout planning, script preparation contracts, a script-owned external payload boundary, a Terminal.Gui command service, and local operator diagnostics. The product roadmap lives in `docs/plan.md`, and the local implementation tracker lives in `workflow/build/implementation-plan.md`.

## Operator Diagnostics

Run `dispatch doctor` to check local prerequisites before executing endpoint jobs. It renders a Terminal.Gui diagnostic report for Windows host support, PowerShell availability, PsExec path resolution, local result path writability, and an admin/elevation indicator. It does not remediate configuration or scan endpoints.

## Run Output

Every command-service path is operator-facing Terminal.Gui UI: root help, command help, version, validation errors, dry-run plans, doctor reports, interactive setup, progress, retained dashboard, compact progress, and final run summaries. Dispatch does not use raw JSON, default parser help, or plain status lines as the console UX.

Running `dispatch` with no arguments opens the interactive command center as a Terminal.Gui retained terminal app. It is not a chain of prompts: menu navigation, doctor diagnostics, command help, and run setup stay inside one in-place console application with menu/status bars, windows, list views, form rows, progress bars, keyboard navigation, and `Ctrl+R` to launch the shared Dispatch run path.

Terminal.Gui view roles are explicit. The application shell owns command-center navigation. Menu and status bars own global actions. Windows/frames own dashboard sections. List views own target, plan, and result rows. Progress bars own dry-run and per-target execution progress. Dry-run renders visible progress before the execution-plan view; non-live or redirected sessions get one designed Terminal.Gui-compatible snapshot instead of repeated output. Real `dispatch run` executions render run identity, transport, target count, elapsed time, per-target phases, aggregate status counts, status symbols, recent activity, and failure summaries. Durable `results.json`, `results.csv`, per-target `result.json`, and captured stdout/stderr files remain in the run folder for automation and troubleshooting.

## Script-Owned Payloads

Dispatch v1 copies only the selected PowerShell script. Installer media, package payloads, Blob/HTTPS/SMB/Azure Files downloads, and payload-specific logging belong inside the script. Pass ordinary non-secret script arguments after `--`; do not pass credentials or SAS tokens on the command line. See `docs/script-owned-payloads.md`.

## Development

```powershell
dotnet build .\Dispatch.sln
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- --help
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- doctor
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- run --dry-run --script .\path\to\script.ps1 --computer-name PC001,PC002 -- -PackageUri https://contoso.example/app.msi
dotnet test .\Dispatch.sln
```
