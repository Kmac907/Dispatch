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

Dispatch has an initial .NET foundation, dry-run request planning, deterministic target resolution, local run layout planning, script preparation contracts, a script-owned external payload boundary, the CLI product surface, and local operator diagnostics. The product roadmap lives in `docs/plan.md`, and the local implementation tracker lives in `workflow/build/implementation-plan.md`.

## Operator Diagnostics

Run `dispatch doctor` to check local prerequisites before executing endpoint jobs. It reports Windows host support, PowerShell availability, PsExec path resolution, local result path writability, and an admin/elevation indicator. It does not remediate configuration or scan endpoints.

## Run Output

Real interactive `dispatch run` executions render a Spectre.Console live dashboard on stderr with run identity, transport, target count, elapsed time, per-target phases, aggregate status counts, recent activity, and failure summaries. When interactive rendering is unavailable, output is redirected, or `--no-dashboard` is passed, Dispatch falls back to append-only stderr status lines. The final result JSON stays on stdout in both modes, so automation should consume stdout for structured results and treat stderr as operator-facing progress and summary output.

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
