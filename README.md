# Dispatch

Dispatch is a Windows-native, push-style remote script execution tool for endpoint administrators. It is inspired by the operational idea of Ansible, but v1 is intentionally narrower: Dispatch is a script orchestrator and executor, not a playbook engine, package manager, configuration-management platform, or endpoint agent.

The active roadmap goal is `6.4` Credential References now that durable run history, log inspection, raw WinRM, and PSRP are in place for the current roadmap scope. Raw `winrm` is implemented and live-validated. PSRP is also implemented for the current roadmap scope: command execution, script execution, remoting-channel artifact collection, structured PowerShell `streamRecords`, shared execution-context configuration-name plumbing, a small auth/connection model surface that explicitly includes later Kerberos and CredSSP placeholders while the current runtime still supports only WSMan plus current-user `Default` or `Negotiate`, and tightened timeout/failure metadata parity are all in place, with elevated live validation succeeding for both the PSRP command and script paths against `82H9704` and `92H9704`. The existing PsExec path remains deferred behind later CLI/module work because the current validation environment does not provide reliable `\\<device>\C$` admin-share staging.

The current implementation still does not provide Dispatch-managed credential/SAS handoff or the later PSRP auth/configuration surface such as explicit credential models, Kerberos, CredSSP, certificate auth, or PSRP-over-SSH. The new PSRP slices currently cover validation/planning acceptance, DI registration, endpoint reachability probing over the default WSMan ports, capability advertisement, real direct command execution over a PSRP remote runspace for `CommandPayload`, runspace-backed remote PowerShell script execution for `ScriptPayload`, transport-local artifact collection over the remoting channel with measurable download progress when the remote archive size is known, structured `streamRecords` capture for current PSRP warning/verbose/debug/information/error output, shared execution-context configuration-name selection that defaults to `Microsoft.PowerShell`, and a shared execution-context auth/connection surface that now explicitly includes future Kerberos and CredSSP placeholders alongside the previously modeled future modes while the implemented runtime still supports only WSMan plus current-user `Default` or `Negotiate`. Unsupported future PSRP auth/connection modes now fail in request validation instead of being silently ignored. Raw WinRM is now a completed transport: it can validate requests, plan runs, probe endpoint reachability, build chunked script-transfer plans, upload the prepared script without SMB/admin shares, execute the prepared PowerShell script over raw WinRM shell semantics, execute direct command payloads over the same raw shell path, classify shell timeouts plus shell-open authentication/authorization/transport failures into the shared result model, collect declared/default artifact folders over the WinRM channel, and write a durable `Admin\events.ndjson` run-history stream alongside the final `Admin\results.json` summary. An elevated live raw WinRM `run cmd whoami` validation now succeeds against `82H9704` and `92H9704`. PsExec also remains a completed transport path today.

## V1 Distribution Roadmap

The planned v1 distribution model is source install from a private Azure DevOps repository. This is not implemented yet; the current repo does not contain the PowerShell module or packaging scripts.

```powershell
git clone https://dev.azure.com/<org>/<project>/_git/Dispatch
cd Dispatch
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- --help
```

Future `bootstrap-install.ps1` work is tracked in Roadmap `8`. It will build `dispatch.exe`, assemble the PowerShell module, install it into the selected module scope, validate that the module and bundled executable work, then remove the cloned source tree including the bootstrap script itself.

Future `install-from-source.ps1` work is also tracked in Roadmap `8` as the reusable build/install helper for developers and CI jobs that intentionally keep the source checkout.

## Long-Term Distribution Roadmap

The long-term enterprise distribution model is an Azure Artifacts PowerShell repository backed by a NuGet feed. This is post-MVP Roadmap `13` work and is not implemented yet.

```powershell
# Future command shape after Roadmap 13 is implemented:
Register-PSResourceRepository `
  -Name DispatchInternal `
  -Uri "https://pkgs.dev.azure.com/<org>/<project>/_packaging/<feed>/nuget/v3/index.json" `
  -Trusted

Install-PSResource Dispatch -Repository DispatchInternal -Scope CurrentUser
Import-Module Dispatch
dispatch --help
```

## Project Status

Dispatch has an initial .NET foundation, dry-run request planning, deterministic target resolution, local run layout planning, script preparation contracts, a script-owned external payload boundary, local operator diagnostics, a Spectre.Console.Cli route for the documented command tree, initial structured output modes, current-path output-control flags, current-path NDJSON event streaming, initial inventory/target selectors for `run ps`, initial YAML inventory transport precedence from defaults/group vars/host vars, YAML inventory credential-reference validation for `credential: <name>` with plaintext secret-field rejection, a Spectre.Console live run surface, a durable `Admin\events.ndjson` canonical run log, a reduced `Admin\results.json` summary, functional `dispatch logs list`, `dispatch logs show latest`, `dispatch logs tail`, `dispatch logs export`, and read-only `dispatch logs retry` retry-plan readers/exports over the local run-history layout, a credential command surface that reports configured provider availability and can store reference-name/username metadata through a file-backed provider without plaintext secret storage, a completed PSRP path for shared-path command/script execution plus artifact collection and `streamRecords`, and a completed raw WinRM path that validates requests, probes endpoint reachability, prepares chunked upload plans, uploads the prepared script, executes that prepared PowerShell script over raw WinRM shell semantics, executes direct command payloads over the same WinRM channel, classifies WinRM shell timeouts plus shell-open authentication/authorization/transport failures into the shared result model, and pulls declared/default artifact folders back over the same WinRM channel. The legacy Terminal.Gui command-service implementation has been removed from the active CLI. PsExec, raw WinRM, and PSRP are the three current transport paths today. The next CLI roadmap work is now remaining credential config policy/reference resolution, YAML jobs, push/hosts/init behavior, and the documented enterprise exit-code/policy behavior. The product roadmap lives in `docs/plan.md`, the CLI design contract lives in `docs/cli-design.md`, the output model lives in `docs/output-and-results.md`, and the local implementation tracker lives in `workflow/build/implementation-plan.md`.

The planned PowerShell module is documented as a thin wrapper over the command tree, not as a separate interactive shell. After module installation, `dispatch.exe` remains the canonical CLI entrypoint, while wrapper functions are expected to map directly to `run ps`, `run cmd`, `run exe`, `apply`, `doctor`, and `version`.

## Operator Diagnostics

Run `dispatch doctor` to check local prerequisites before executing endpoint jobs. The redesigned command surface renders diagnostics through Spectre.Console and supports stable structured output modes as they are implemented. Diagnostics do not remediate configuration or scan endpoints.

## Run Output

The active CLI design uses Spectre.Console.Cli for command routing and Spectre.Console for operator output. Rich terminal output is for humans; `--output json`, `--output ndjson`, and `--output yaml` are implemented for the current `run ps` plan/result paths. NDJSON still streams typed stdout events for the current command path, and runs now also persist a durable `Admin\events.ndjson` event stream plus a reduced `Admin\results.json` summary file. Optional duplicate files such as `results.csv`, `dispatch.log`, and per-target `result.json` are no longer written by default. The final rich/table summary prints an `Outputs` panel with the run-local `results.json` path, the durable `events.ndjson` path, the per-target root, and separate `stdout.txt` / `stderr.txt` locations so operators can jump straight from the completion summary to the persisted files.

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

Spectre live output is event-driven. Planning and preflight use live `Status`/`Progress` only when the work is actually happening. Real execution uses one `LiveDisplay` dashboard fed by internal run events plus a one-second heartbeat refresh, so elapsed timers continue moving during long phases; workers do not write directly to the console. The live dashboard shows aggregate counts, one measurable completion bar, active-first target ordering, per-target current-phase elapsed time, and recent events. Measured per-target progress is shown only when Dispatch has a real denominator; today that includes WinRM chunk upload counts during script transfer, WinRM artifact download bytes when the archive size is known, and PSRP artifact download bytes when the archive size is known. After live rendering ends, Dispatch prints a stable final summary that includes the target outcome table plus an `Outputs` panel for the key result/log file locations, then keeps the durable logs/results for automation and troubleshooting.

Run from a real Windows Terminal or PowerShell window, not from redirected output:

```powershell
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- --help
```

Expected behavior for the completed Roadmap 6 redesign: Spectre help shows the documented command tree. During actual runs, live progress appears only while planning/preflight/execution work is active. If output is redirected or `--no-progress` is supplied, Dispatch uses stable non-live output.

Current implementation boundary: the documented command tree is registered through Spectre.Console.Cli. `dispatch run ps <script.ps1>`, `dispatch run cmd <command>`, and `dispatch run exe <path>` now route through the shared planner/executor path when the selected transport supports the requested payload; today that means raw WinRM for script and command payloads, and PSRP for script and command payloads plus artifact collection, structured `streamRecords`, optional shared execution-context configuration-name selection that defaults to `Microsoft.PowerShell`, and an auth/connection execution-context surface that explicitly includes later Kerberos and CredSSP placeholders while the implemented runtime still supports only WSMan plus current-user `Default` or `Negotiate`, with unsupported future auth/connection selections failing during request validation. `dispatch logs list`, `dispatch logs show latest`, `dispatch logs tail`, `dispatch logs export [run-id|latest] --dest <path>`, and `dispatch logs retry [run-id|latest]` now read the local run-history layout; export writes a per-run folder containing `results.json`, `events.ndjson` when present, and `results.csv`, while retry emits a read-only retry plan and manual command hint when the original command payload can be reconstructed. `dispatch creds add|list|test|remove` now call the credential provider abstraction and clearly report provider availability; configuring `Dispatch:CredentialProvider` as `file` or `local` stores reference names and optional username metadata at `Dispatch:CredentialStorePath`, defaulting to `C:\ProgramData\Dispatch\Credentials\references.json`. YAML inventories in the current supported subset accept `credential: <name>` reference names on defaults, group vars, host vars, and hosts while rejecting plaintext secret-like inventory fields. This surface does not implement plaintext password flags, YAML job credential resolution, provider-backed secret storage, runtime secret resolution, or transport credential handoff. `dispatch doctor` and `dispatch version` are functional. The compatibility syntax `dispatch run --script <path> --computer-name <names>` is still supported through the existing Dispatch parser. `apply`, `push`, `hosts`, and `init` remain planned behavior surfaces and must fail clearly until their roadmap slices are implemented.

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
