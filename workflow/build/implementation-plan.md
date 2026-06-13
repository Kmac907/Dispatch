# Dispatch Local Implementation Plan

This file is intentionally local execution context. It tracks implementation progress against the authoritative roadmap in `docs/plan.md`.

## Tracking Rules

- Update this file after completing a meaningful implementation slice.
- Keep roadmap IDs identical to `docs/plan.md`.
- Use only these statuses: `not_started`, `in_progress`, `partial`, `completed`, `blocked`.
- Record what shipped, what remains, and what should be done next.
- Keep remaining gaps behavior-based, not task-list based.

## Reference Scope

Primary product scope and acceptance criteria live in `docs/plan.md`.

## Current Status

Dispatch is in planning state. No buildable product slices are recorded as complete in this tracker yet.

The next active roadmap item is `1`: Foundation And Repository Structure.

## Roadmap Tracker

### MVP Roadmap

#### 1. Foundation And Repository Structure

Status: `not_started`

Objective:
Create the buildable .NET solution, project layout, shared models, and test structure for Dispatch.

Scope:
- Create `Dispatch.Core`, `Dispatch.Transports.PsExec`, `Dispatch.Cli`, and test projects.
- Establish shared job, target, script, transport, execution, and result model types.
- Add dependency injection, logging abstractions, JSON serialization, and basic configuration defaults.
- Add initial build/test commands.

Non-goals:
- No real remote execution.
- No interactive CLI workflow beyond a placeholder command surface.
- No PowerShell module or packaging.

Dependencies:
- None.

Definition of done:
- Solution builds cleanly.
- Unit test project runs.
- Core model types can serialize to JSON.
- CLI can start and print version/help without executing a job.

#### 1.1 Job And Result Model

Status: `not_started`

Objective:
Represent a Dispatch run as a structured job with per-target execution state and serializable results.

Scope:
- Define `DispatchJob`, `DispatchRequest`, `ExecutionPlan`, `TargetExecution`, and result models.
- Define `ScriptPayload` and `CommandPayload`, with v1 validation allowing only supported transport/payload combinations.
- Define target states: `Pending`, `Resolving`, `Probing`, `PreparingScript`, `Executing`, `CollectingArtifacts`, `Succeeded`, `Failed`, `TimedOut`, `Cancelled`.
- Define stable result JSON fields, flattened CSV fields, and common failure category enum values.
- Define expected exit code policy with default `[0]`.
- Add JSON round-trip tests.

Non-goals:
- No endpoint probing.
- No script transfer.
- No transport execution.

Dependencies:
- 1.

Definition of done:
- Job/result models compile and are covered by unit tests.
- JSON output is stable enough for initial CLI and PowerShell wrapper consumption.
- Result objects can represent success, failure, timeout, and cancellation.
- Result objects include the minimum schema fields and common failure categories from `docs/plan.md`.

#### 1.2 Application Host And Configuration

Status: `not_started`

Objective:
Create the shared CLI host wiring used by both interactive and automation modes.

Scope:
- Wire `Microsoft.Extensions.Hosting`.
- Register core planner/executor services.
- Register logging and configuration providers.
- Add default values for local run root, remote script root, transport, throttle, and expected exit codes.

Non-goals:
- No user configuration file editor.
- No registry or machine-wide config store.
- No secret storage.

Dependencies:
- 1.1.

Definition of done:
- CLI resolves services through DI.
- Defaults are available through typed options.
- Tests cover default configuration binding where practical.

#### 2. Request Planning And Dry Run

Status: `not_started`

Objective:
Convert CLI/module input into a dry-run-capable execution plan before touching endpoints.

Scope:
- Validate script path, script arguments, output root, remote script root, transport selection, run context, throttle limit, and expected exit codes.
- Validate payload support using the transport/payload support matrix before endpoint probing or remote work.
- Normalize direct execution command shape.
- Generate run ID and planned local/remote paths.
- Add dry-run result output.

Non-goals:
- No endpoint access.
- No PsExec process launch.
- No script copy to endpoints.

Dependencies:
- 1.2.

Definition of done:
- `dispatch run --dry-run` can print or write a complete execution plan.
- Invalid local inputs fail before endpoint work starts.
- Unsupported transport/payload combinations fail before endpoint work starts.
- Tests cover command/path planning and expected exit code policy.

#### 2.1 Target Resolution

Status: `not_started`

Objective:
Resolve target names from command-line input and target files in deterministic order.

Scope:
- Support `--computer-name PC001,PC002`.
- Support `--target-file`.
- Trim whitespace, ignore blank/comment lines, and de-duplicate case-insensitively while preserving first-seen order.
- Preserve source metadata in the plan.

Non-goals:
- No DNS or network probe.
- No inventory grouping.
- No dynamic discovery.

Dependencies:
- 2.

Definition of done:
- Target resolution produces stable ordered target lists.
- Empty or missing target sources fail clearly.
- Tests cover comma-separated input, target files, comments, blanks, and duplicates.

#### 2.2 Local Run Layout

Status: `not_started`

Objective:
Create the local admin-side run folder structure and planned per-target artifact paths.

Scope:
- Create `C:\ProgramData\Dispatch\Runs\<RunId>\Admin`.
- Create planned target roots under `Targets\<DeviceName>`.
- Support overriding output root for tests and non-default operator workflows.
- Ensure file-vs-directory conflicts fail clearly.

Non-goals:
- No endpoint log copy.
- No CSV/JSON final summary writing yet.
- No retention policy.

Dependencies:
- 2.1.

Definition of done:
- Dry-run and real-run planning create/validate local output paths.
- Tests cover default and overridden run roots.
- The plan includes local admin and per-target result paths.

#### 3. Script Transfer And Remote Run Path

Status: `not_started`

Objective:
Prepare the selected script at a target-local Dispatch run path when the selected transport requires endpoint-local execution.

Scope:
- Build a script execution manifest from `--script` and script arguments.
- Copy only the selected script to the endpoint for PsExec v1.
- Store the script under remote `script`.
- Report script transfer failures per target.
- Preserve direct execution semantics; no generated launcher is required.

Non-goals:
- No installer/media payload staging.
- No Azure Blob source handling.
- No endpoint-side download agent.
- No differential sync.
- No compression requirement.

Dependencies:
- 2.2.

Definition of done:
- A target can receive the selected script under `C:\ProgramData\Dispatch\Runs\<RunId>\script`.
- Script transfer failure prevents execution for that target and is reflected in structured results.
- Tests cover script manifest construction and admin-share path conversion.

#### 3.1 Script-Owned External Payload Contract

Status: `not_started`

Objective:
Make the external payload boundary explicit so installer scripts own Blob/SAS payload retrieval.

Scope:
- Document that Dispatch does not stage installer/media payloads.
- Document that scripts must download/copy their own Blob, HTTPS, SMB, or Azure Files payloads.
- Support passing ordinary script arguments through to the script.
- Keep Dispatch logs focused on orchestration metadata and script output, not payload transfer internals.

Non-goals:
- No automatic payload download.
- No automatic script rewriting.
- No SAS generation, persistence, validation, or refresh.
- No guaranteed Blob/share access from the endpoint.
- No Azure Files identity support.

Dependencies:
- 3.

Definition of done:
- The plan documents the script-owned payload boundary.
- CLI/help text does not advertise Dispatch-owned media payload staging.
- Script arguments are represented in planning and execution without changing the script-owned payload model.

#### 4. PsExec Transport And Direct Execution

Status: `not_started`

Objective:
Run the prepared PowerShell script directly on each endpoint through PsExec.

Scope:
- Resolve explicit/default PsExec path.
- Build PsExec argument arrays without string-concatenation quoting bugs.
- Support current context and `--run-as-system`.
- Preserve the v1 credential boundary: current admin context only, no plaintext password flags, no Dispatch-managed `psexec -u/-p`.
- Preserve the v1 SAS/secret boundary: no supported SAS token handoff and no command-line secret passing.
- Execute `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <prepared script>`.
- Capture process exit code, stdout, stderr, duration, and failure reason.
- Classify expected exit codes as success.

Non-goals:
- No WinRM.
- No managed/harness mode.
- No credential prompting or password handling.
- No SAS token handoff.

Dependencies:
- 3.1.

Definition of done:
- PsExec invocation is test-covered without requiring live endpoints.
- Localhost or shim-based execution can validate success/failure classification.
- Nonzero unexpected exit codes fail the target with clear reason.
- PsExec command rendering and dry-run output cannot expose credentials, SAS tokens, or secret values.
- PsExec prerequisite failures map to stable failure categories such as `ProbeFailed`, `AuthenticationFailed`, `AuthorizationFailed`, or `TransportUnavailable`.

#### 4.1 Endpoint Probe And Execution Guardrails

Status: `not_started`

Objective:
Perform minimum endpoint checks before script transfer/execution and report skipped work clearly.

Scope:
- DNS resolution check.
- Connectivity probe.
- Admin share/script path access check for PsExec mode.
- Classify missing transport prerequisites using the endpoint prerequisite matrix in `docs/plan.md`.
- Skip script transfer/execution when required probes fail.

Non-goals:
- No full readiness inventory.
- No remediation of WinRM/firewall/admin-share configuration.
- No credential delegation configuration.

Dependencies:
- 4.

Definition of done:
- Probe failures produce structured target failures before PsExec starts.
- Tests cover probe result classification using abstractions/mocks where practical.
- Terminal output distinguishes probe, script transfer, and execution failures.

#### 5. Batch Orchestration And Results

Status: `not_started`

Objective:
Run jobs across multiple targets with bounded concurrency and durable admin-side result output.

Scope:
- Implement throttle-limited target worker pool.
- Maintain per-target state transitions.
- Collect stdout/stderr and endpoint artifacts where available.
- Write `dispatch.log`, `results.csv`, `results.json`, and per-target `result.json`.
- Print concise terminal summary.

Non-goals:
- No retry policy.
- No timeout/cancellation beyond basic process completion behavior unless already trivial.
- No full-screen TUI.

Dependencies:
- 4.1.

Definition of done:
- Multi-target runs respect throttle limit.
- Result JSON and CSV include run metadata, target metadata, statuses, exit codes, durations, and failure reasons.
- A partial failure job can be distinguished from full success and full failure.

#### 5.1 Artifact Copy-Back

Status: `not_started`

Objective:
Pull declared or default endpoint output artifacts back to the admin run folder.

Scope:
- Copy remote `logs` and `artifacts` folders when present.
- Store copied files under local `Targets\<DeviceName>`.
- Track artifact copy success/failure independently from script execution.

Non-goals:
- No complex glob rule engine in MVP unless simple patterns are already available.
- No log streaming.
- No retention management.

Dependencies:
- 5.

Definition of done:
- Missing artifact folders do not hide the script execution result.
- Copy-back-only failures are represented separately from script failures.
- Tests cover path planning and copy failure classification.

#### 6. CLI Product Surface

Status: `not_started`

Objective:
Provide both automation and interactive command surfaces over the same core request/execution path.

Scope:
- Implement `dispatch run`.
- Implement `dispatch doctor`.
- Run active interactive CLI when no arguments are supplied.
- Use `System.CommandLine` for command parsing/help.
- Use `Spectre.Console` for prompts, confirmations, tables, progress/status output, and readable terminal summaries.
- Fall back to simple append-only output when interactive rendering is unavailable.

Non-goals:
- No GUI.
- No full-screen terminal application.
- No separate interactive execution engine.

Dependencies:
- 5.1.

Definition of done:
- `dispatch` guides the user through script, targets, transport, run context, throttle, dry-run, and confirmation.
- `dispatch run` supports non-interactive automation.
- Both modes create the same request model and call the same core planner/executor.

#### 6.1 Operator Diagnostics

Status: `not_started`

Objective:
Give operators a quick way to validate local prerequisites and common configuration problems.

Scope:
- Implement `dispatch doctor`.
- Check OS, PowerShell availability, PsExec path/configuration, default output path access, and basic admin context indicators.
- Redact secrets and sensitive paths where needed.

Non-goals:
- No automatic environment repair.
- No endpoint fleet scan.
- No Azure login validation in MVP.

Dependencies:
- 6.

Definition of done:
- `dispatch doctor` returns a clear success/failure summary.
- Missing PsExec or inaccessible default paths are reported with actionable messages.
- PowerShell wrapper can expose the same behavior through `Test-Dispatch`.

#### 7. PowerShell Module Wrapper

Status: `not_started`

Objective:
Provide PowerShell-friendly commands that wrap the bundled `dispatch.exe`.

Scope:
- Add `Dispatch.psd1` and `Dispatch.psm1`.
- Bundle `dispatch.exe` under `bin\win-x64`.
- Implement `Start-Dispatch`, `Invoke-DispatchScript`, `Invoke-DispatchJob`, and `Test-Dispatch`.
- Ensure `Start-Dispatch` passes no arguments.
- Prefer explicit JSON result path for automation functions rather than parsing live console output.

Non-goals:
- No binary cmdlets in MVP.
- No PowerShell repository publishing in MVP.
- No separate PowerShell execution engine.

Dependencies:
- 6.1.

Definition of done:
- Importing the module exposes the documented commands.
- `Start-Dispatch` launches the active CLI.
- `Invoke-DispatchScript` invokes `dispatch run` and returns machine-readable results.

#### 8. Source Install And Local Packaging

Status: `not_started`

Objective:
Produce a tested source-install flow that builds Dispatch from a private Azure Repos clone, installs the PowerShell module locally, verifies the installation, and removes source artifacts after bootstrap installation.

Scope:
- Build single-file self-contained `dispatch.exe`.
- Assemble module package layout.
- Add `install-from-source.ps1` for use inside an existing clone.
- Add `bootstrap-install.ps1` as the primary operator flow for build/install/validate/cleanup from an existing private Azure Repos clone.
- Add `install.ps1` for installing an already assembled module package into `CurrentUser` or `AllUsers` module scopes.
- Validate the built executable, module manifest, module import, and exported commands before reporting success.
- Use a cleanup helper outside the source tree to remove the temporary source clone and bootstrap script copy after successful bootstrap installation.
- Support `-NoCleanup` for developers and troubleshooting.
- Create `Dispatch-<version>-win-x64.zip` as an optional release convenience artifact.

Non-goals:
- No MSI installer.
- No Azure Artifacts feed.
- No WinGet package.

Dependencies:
- 7.

Definition of done:
- A clean machine with Azure Repos access, Git, PowerShell, and the .NET SDK can clone the repo, run `bootstrap-install.ps1`, and run `Start-Dispatch`.
- Bootstrap installation builds the project and module, validates the installation, changes out of the source directory, invokes an external cleanup helper, and cleans up the cloned source tree including the bootstrap script itself.
- Cleanup failure is reported without uninstalling an already validated module.
- Install scripts validate the module manifest, copied EXE, import behavior, and exported commands.
- Pipeline or build script can create the optional ZIP without manual assembly.

### Post-MVP Roadmap

#### 9. PSRP Transport

Status: `not_started`

Objective:
Add PowerShell Remoting Protocol execution while preserving the same job, script execution, command execution, result, and artifact model.

Scope:
- Implement `Dispatch.Transports.Psrp` using `Microsoft.PowerShell.SDK`.
- Use `WSManConnectionInfo` and remote runspaces for PSRP-over-WSMan.
- Support `--transport psrp`.
- Support direct PowerShell command execution.
- Support direct PowerShell script execution.
- Support target configuration names such as `Microsoft.PowerShell` and later `PowerShell.7`.
- Capture output, error, warning, verbose, debug, and information streams where practical.
- Map PowerShell stream output, terminating errors, exit codes, and duration into the common target result model.
- Support current user / default Negotiate authentication first.
- Add the model surface for later explicit `PSCredential`, certificate authentication, and PSRP-over-SSH.
- Implement PSRP against the common transport result contracts from `docs/plan.md`.
- Define PSRP credential handling: secure prompt or `PSCredential` only, no plaintext password flags, no automatic remoting/CredSSP/delegation policy changes.
- Define PSRP SAS/secret handling: retrieve secret locally, protect it, transfer the protected secret file over the PSRP remoting channel, invoke the script with only `-SecretFile <path>`, and allow optional future in-memory parameter handoff only when fully redacted.
- Preserve the script-owned external payload boundary; PSRP does not make Dispatch responsible for Blob/SAS payload retrieval.

Non-goals:
- No raw WinRM shell transport in this slice.
- No CredSSP/delegation automation by default.
- No WinRM or PowerShell remoting policy remediation.
- No Linux/macOS SSH target support.
- No endpoint-side Key Vault login requirement.

Dependencies:
- 8.

Definition of done:
- The same request can run through `--transport psrp`.
- PSRP results map into the same target result model as PsExec.
- Command and script execution both work through the same request/execution path.
- Tests cover runspace connection planning, configuration name handling, stream mapping, error mapping, and result serialization.
- Tests cover redacted credential and SAS/secret handoff planning.
- Tests cover the common transport result contract for PSRP.

#### 9.1 Raw WinRM Transport

Status: `not_started`

Objective:
Add Ansible-style raw WinRM shell/command execution while preserving the same job, script execution, command execution, result, and artifact model.

Scope:
- Implement `Dispatch.Transports.WinRm` as a raw WS-Management shell/command transport.
- Prefer a .NET wrapper around WinRM Client Shell API / WS-Management shell-command semantics.
- Support `--transport winrm`.
- Create remote shells and commands through WinRM shell/command semantics.
- Support direct command execution.
- Support PowerShell script execution by invoking `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <prepared script>`.
- Implement streamed/chunked script upload without requiring SMB/admin shares.
- Implement streamed/chunked artifact collection without requiring SMB/admin shares.
- Capture process stdout, stderr, exit code, duration, and failure reason.
- Support current user / default Negotiate authentication first.
- Add the model surface for later Kerberos, NTLM, Basic-over-HTTPS, certificate auth, and CredSSP where explicitly enabled.
- Keep `winrs.exe` as a diagnostic shim or test fixture only, not the primary implementation.
- Do not use PowerShell SDK runspaces in raw WinRM; that belongs to `Dispatch.Transports.Psrp`.
- Define raw WinRM credential handling: secure prompt or protected credential object only, no plaintext password flags, Basic only over HTTPS, no automatic WinRM/CredSSP/delegation policy changes.
- Define raw WinRM SAS/secret handling: retrieve secret locally, protect it, upload the protected secret file through streamed/chunked WinRM content, invoke the remote process with only `-SecretFile <path>`, and keep raw tokens out of command lines and result output.
- Preserve the script-owned external payload boundary; raw WinRM does not make Dispatch responsible for Blob/SAS payload retrieval.

Non-goals:
- No PowerShell SDK runspace execution in this slice.
- No rich PowerShell stream mapping beyond process stdout/stderr.
- No CredSSP/delegation automation by default.
- No WinRM policy remediation.
- No Linux/macOS target support.

Dependencies:
- 9.

Definition of done:
- The same request can run through `--transport winrm`.
- Raw WinRM results map into the same target result model as PsExec and PSRP.
- Command and script execution both work through the same request/execution path.
- Tests cover shell/command planning, timeout classification, chunked transfer planning, stdout/stderr/exit-code mapping, and result serialization.
- Tests cover redacted credential and SAS/secret handoff planning.

#### 10. Script-Owned Payload Documentation And Guardrails

Status: `not_started`

Objective:
Document and enforce the boundary that external payload retrieval belongs to the script, while adding a narrow runtime secret handoff model for scripts that need SAS-backed payload access.

Scope:
- Document recommended patterns for scripts that download payloads from Blob storage with SAS access.
- Add a post-MVP Key Vault secret source that Dispatch can use on the admin workstation or jump box to retrieve a SAS once per job.
- Pass retrieved secrets to scripts as runtime inputs through a redacted mechanism such as a protected temporary secret file or environment variable.
- Keep examples focused on passing script arguments, environment values, or protected secret-file paths into scripts.
- Add warnings in docs/help text that scripts should not write SAS tokens to logs.
- Redact secret values from console output, logs, result JSON, CSV summaries, dry-run output, and transport command rendering.

Non-goals:
- No Dispatch-owned Blob download implementation.
- No Azure Files SMB identity framework.
- No general-purpose secret vault system.
- No endpoint-side Key Vault login requirement for the default model.
- No command-line secret passing as the preferred PsExec model.
- No SAS generation, persistence, validation, or refresh.
- No long-lived SAS storage.
- No installer/media payload staging.

Dependencies:
- 8.

Definition of done:
- Documentation includes script-owned Blob/SAS payload examples.
- Dispatch can retrieve a configured Key Vault secret before a job and expose it to the script without logging the value.
- PsExec and WinRM plans show redacted secret handoff behavior.
- CLI/help text does not imply Dispatch owns Blob payload retrieval.
- The roadmap keeps Blob payload orchestration out of Dispatch scope.

#### 11. Managed Execution Mode

Status: `not_started`

Objective:
Add optional harness-based managed execution for stronger normalized results.

Scope:
- Add `-ExecutionMode Managed` / `--execution-mode managed`.
- Generate a minimal internal harness only when managed mode is selected.
- Standardize `stdout.txt`, `stderr.txt`, `result.json`, timeout behavior, and child process exit classification.

Non-goals:
- Managed mode is not the default.
- No script rewriting.
- No replacement for direct execution mode.

Dependencies:
- 9.

Definition of done:
- Direct mode remains unchanged.
- Managed mode produces consistent result files across PsExec, PSRP, and raw WinRM.
- Tests cover success, nonzero exit, terminating exception, and timeout classification.

#### 12. Job Manifest

Status: `not_started`

Objective:
Allow repeatable jobs to be described in YAML or JSON without becoming a playbook engine.

Scope:
- Define minimal job manifest schema for targets, transport, script, script arguments, execution context, expected exit codes, and artifacts.
- Add `dispatch run --job <path>`.
- Add `Invoke-DispatchJob`.
- Add schema file under module `schemas`.

Non-goals:
- No task graph.
- No conditionals, loops, facts, handlers, roles, or idempotency framework.
- No Ansible-style module ecosystem.

Dependencies:
- 8.

Definition of done:
- A manifest can run the same job as CLI parameters.
- Schema validation reports actionable errors.
- Manifest and CLI inputs converge into the same `DispatchRequest` model.

#### 13. Enterprise Distribution

Status: `not_started`

Objective:
Add enterprise-grade distribution channels after the source-install and local packaging flow is stable.

Scope:
- Azure Artifacts PowerShell repository publishing backed by a NuGet feed.
- Command-line installation through `Register-PSResourceRepository` and `Install-PSResource`.
- CI package publishing through `Publish-PSResource` or equivalent NuGet package publishing.
- MSI installer.
- Optional WinGet/private package feed.
- Optional binary PowerShell cmdlets that call `Dispatch.Core` directly.

Non-goals:
- No public gallery publishing unless explicitly approved.
- No forced company repository requirement for local install.
- No endpoint agent installer.

Dependencies:
- 8.

Definition of done:
- Azure Artifacts feed can publish and install the module package as a PowerShell resource.
- Operators can install with `Install-PSResource Dispatch -Repository <DispatchFeed>`.
- MSI install/uninstall works for all-user installation.
- Existing source install and optional ZIP install remain supported.

## Completed Slices

- None yet.

## Remaining Gaps

- Dispatch does not yet have a buildable .NET solution.
- Dispatch does not yet have a typed job/result model.
- Dispatch cannot yet convert operator input into an execution plan.
- Dispatch cannot yet resolve targets from names or files.
- Dispatch cannot yet create local run folders.
- Dispatch cannot yet prepare scripts on endpoints.
- Dispatch cannot yet execute prepared scripts through PsExec.
- Dispatch cannot yet orchestrate multi-target jobs.
- Dispatch cannot yet emit durable CSV/JSON/admin results.
- Dispatch does not yet have an active CLI or automation CLI.
- Dispatch does not yet have a PowerShell module wrapper.
- Dispatch does not yet have source-install, bootstrap-install, or optional ZIP packaging.
- Post-MVP transports, script-owned payload documentation, managed execution, manifests, and enterprise distribution are not implemented.

## Suggested Next Sequence

1. `1` Foundation And Repository Structure
2. `1.1` Job And Result Model
3. `1.2` Application Host And Configuration
4. `2` Request Planning And Dry Run
5. `2.1` Target Resolution

## Full Remaining Ordered Sequence

1. `1` Foundation And Repository Structure
2. `1.1` Job And Result Model
3. `1.2` Application Host And Configuration
4. `2` Request Planning And Dry Run
5. `2.1` Target Resolution
6. `2.2` Local Run Layout
7. `3` Script Transfer And Remote Run Path
8. `3.1` Script-Owned External Payload Contract
9. `4` PsExec Transport And Direct Execution
10. `4.1` Endpoint Probe And Execution Guardrails
11. `5` Batch Orchestration And Results
12. `5.1` Artifact Copy-Back
13. `6` CLI Product Surface
14. `6.1` Operator Diagnostics
15. `7` PowerShell Module Wrapper
16. `8` Source Install And Local Packaging
17. `9` PSRP Transport
18. `9.1` Raw WinRM Transport
19. `10` Script-Owned Payload Documentation And Guardrails
20. `11` Managed Execution Mode
21. `12` Job Manifest
22. `13` Enterprise Distribution

## Current Recommendation

Start with `1` Foundation And Repository Structure.

Reasoning:

- Every later slice depends on a buildable solution, project boundaries, shared models, and test structure.
- The first useful vertical path is dry-run planning before endpoint work.
- PsExec execution should not begin until request planning, target resolution, local run layout, and script transfer/preparation are stable.
- The PowerShell module and ZIP installer should wrap a working CLI rather than define separate behavior.

## Session Resume Notes

- Keep `docs/plan.md` as the source-of-truth roadmap.
- Keep this file as the local execution tracker.
- Preserve identical roadmap IDs between both files.
- Keep commits small and focused.
- Prefer unit tests around planning, path mapping, status classification, and transport argument construction before live endpoint validation.
