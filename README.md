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

Dispatch has an initial .NET foundation, dry-run request planning, deterministic target resolution, local run layout planning, script preparation contracts, a script-owned external payload boundary, local operator diagnostics, a Spectre.Console.Cli route for the documented command tree, initial structured output modes, current-path output-control flags, current-path NDJSON event streaming, initial inventory/target selectors for `run ps`, and a Spectre.Console live run surface. The legacy Terminal.Gui command-service implementation has been removed from the active CLI. The remaining CLI roadmap includes durable run logs, deeper inventory selectors, YAML jobs, logs, credentials, push/hosts/init behavior, and the documented enterprise exit-code/policy behavior. The product roadmap lives in `docs/plan.md`, the CLI design contract lives in `docs/cli-design.md`, and the local implementation tracker lives in `workflow/build/implementation-plan.md`.

## Operator Diagnostics

Run `dispatch doctor` to check local prerequisites before executing endpoint jobs. The redesigned command surface renders diagnostics through Spectre.Console and supports stable structured output modes as they are implemented. Diagnostics do not remediate configuration or scan endpoints.

## Run Output

The active CLI design uses Spectre.Console.Cli for command routing and Spectre.Console for operator output. Rich terminal output is for humans; `--output json`, `--output ndjson`, and `--output yaml` are implemented for the current `run ps` plan/result paths. NDJSON streams typed stdout events for the current command path; durable run-history event files remain roadmap work.

The target command tree is:

```text
dispatch apply <job.yml>
dispatch run ps <script.ps1>
dispatch run cmd <command>
dispatch run exe <path>
dispatch push <source> --dest <remote-path>
dispatch hosts list|test|validate|graph|vars
dispatch logs list|show|tail|export|retry
dispatch creds add|list|test|remove
dispatch doctor
dispatch init job|hosts|config|all
dispatch version
```

### Live Terminal Design

Spectre live output is event-driven. Planning and preflight use live `Status`/`Progress` only when the work is actually happening. Real execution uses one `LiveDisplay` dashboard fed by internal run events; workers do not write directly to the console. After live rendering ends, Dispatch prints a stable final summary and keeps durable logs/results for automation and troubleshooting.

Run from a real Windows Terminal or PowerShell window, not from redirected output:

```powershell
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- --help
```

Expected behavior for the completed Roadmap 6 redesign: Spectre help shows the documented command tree. During actual runs, live progress appears only while planning/preflight/execution work is active. If output is redirected or `--no-progress` is supplied, Dispatch uses stable non-live output.

Current implementation boundary: `dispatch run ps <script.ps1> --plan`, `dispatch doctor`, and `dispatch version` are registered through Spectre.Console.Cli. The compatibility syntax `dispatch run --script <path> --computer-name <names>` is still supported through the existing Dispatch parser. `apply`, `push`, `hosts`, `logs`, `creds`, `init`, `run cmd`, and `run exe` remain planned surfaces and must fail clearly until their roadmap slices are implemented.

## Script-Owned Payloads

Dispatch v1 copies only the selected PowerShell script. Installer media, package payloads, Blob/HTTPS/SMB/Azure Files downloads, and payload-specific logging belong inside the script. Pass ordinary non-secret script arguments after `--`; do not pass credentials or SAS tokens on the command line. See `docs/script-owned-payloads.md`.

## Development

```powershell
dotnet build .\Dispatch.sln
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- --help
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- doctor
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- run ps .\path\to\script.ps1 --target PC001,PC002 --plan --output json
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- run --dry-run --script .\path\to\script.ps1 --computer-name PC001,PC002 -- -PackageUri https://contoso.example/app.msi
dotnet test .\Dispatch.sln
```
