# Roadmap Status

This page is a human-readable status summary. The detailed roadmap remains in `docs/plan.md`.

## Works Now

- Buildable .NET solution.
- Core job/result models.
- Shared planner/executor path.
- Deterministic target resolution.
- Local run folder layout.
- Direct PsExec execution plumbing and guardrails.
- Raw WinRM transport for command/script execution, remote upload, artifacts, and supported credential providers.
- PSRP transport for command/script execution, streams, artifacts, and supported credential providers.
- Canonical `Admin\events.ndjson` and reduced `Admin\results.json`.
- Per-target `stdout.txt` / `stderr.txt`.
- Spectre.Console.Cli command tree registration.
- Live dashboard rendering model.
- Structured output modes on current paths.
- Inventory subset for current run paths.
- `dispatch apply <job.yml> --plan`, `dispatch apply <job.yml> --check`, and `dispatch apply <job.yml>` for selected multi-task script-first `ps`, scalar `cmd`, and scalar `exe` jobs, plus plan/check-only `copy` task rendering.
- `dispatch push <source> --dest <remote-path>` for single-file and recursive directory transfer over raw WinRM or PSRP, including `--transport auto` selection through inventory/config/default transport policy, optional replacement through `--overwrite`, target-local pre-replacement backup through `--backup`, explicit push-result SHA-256 enforcement/reporting through `--checksum`, single-file `.ps1` execute-after-copy through `--execute`, post-execute script removal through `--cleanup`, plus push `--plan` / `--check` preview.
- `dispatch hosts list` and `dispatch hosts validate` local inventory inspection.
- `dispatch hosts test` transport endpoint probing for selected inventory targets.
- `dispatch hosts graph` local inventory group/child relationship inspection.
- `dispatch hosts vars` local effective host metadata inspection.
- `dispatch doctor --transport auto|psexec|psrp|winrm --output rich|table|json|ndjson|yaml` local prerequisite scoping, stable structured output, runtime/context/policy diagnostics, global Dispatch config parseability, run-history layout checks, and PsExec local policy/EULA diagnostics.
- `dispatch init config|hosts|job|all` starter YAML scaffolding.
- Logs commands over local run history.
- Credential references with prompt, DPAPI file, Windows Credential Manager, and Azure Key Vault behavior for PSRP and raw WinRM.
- `dispatch run` execution results, completed `dispatch apply` execution for supported `ps`, `cmd`, and `exe` tasks, completed `dispatch push` transfer/execute results, and `dispatch hosts test` endpoint-probe results return stable process exit codes for current result categories: `0` success, `2` host/execution failure, `3` probe/unreachable/timeout, `4` authentication/authorization, `5` transport unavailable, `6` cancelled, and `10` internal error. Usage/config/inventory/YAML/planning and local lifecycle/inspection command failures remain `1`.
- `dispatch run --system` is policy-gated for PsExec LocalSystem execution and returns policy exit code `7` before planning or endpoint work when policy approval is missing or the selected transport does not support LocalSystem.
- Omitted `--transport` and `--transport auto` cannot implicitly select PsExec unless fallback policy is approved with `dispatch.allow_psexec_fallback: true` in config or supported inventory `allow_psexec_fallback: true`; explicit `--transport psexec` remains the CLI opt-in. Missing approval returns policy exit code `7` before planning or endpoint work.
- Dispatch-owned rich/table output, structured JSON/NDJSON/YAML output, durable `Admin\events.ndjson`, durable `Admin\results.json`, optional per-target `result.json`, optional `Admin\results.csv`, and optional `Admin\dispatch.log` redact secret-looking values before writing.

## Partial

- `doctor` supports local transport-scoped prerequisite checks, stable output modes, global Dispatch config parseability, run-history layout checks, and PsExec local policy/EULA diagnostics, but credential-provider/host-schema diagnostics and some broader checks remain planned.
- `push` supports file and recursive directory transfer over raw WinRM or PSRP, including `--transport auto` selection through inventory/config/default transport policy, `--backup` pre-replacement backup, `--checksum` push-result SHA-256 enforcement/reporting, single-file `.ps1` execute-after-copy through `--execute`, and post-execute script removal through `--cleanup`. Directory cleanup and PsExec push remain deferred unless their boundaries are explicitly reopened.
- `hosts` command group has current `list`, `validate`, `graph`, `vars`, and transport-probe `test` over the supported v1 inventory subset.
- Inventory support covers the v1 subset needed by current run paths.
- YAML task execution beyond `ps`/`cmd`/`exe`, including real `copy` transfer execution, remains planned.
- Credential handoff is not complete for every entry point; PSCredential wrapper handoff remains planned. PsExec explicit password handoff remains intentionally unsupported under the current no-plaintext PsExec boundary.
- Script secret handoff is separate from endpoint `--credential`. The approved initial boundary is current `dispatch run ps ... --secret name=reference` plan/dry-run validation and redacted rendering of script-parameter bindings; real execution is blocked until the later safe provider-resolution and transport-parameter-binding slice.
- Raw `stdout.txt`, raw `stderr.txt`, and copied artifact content are script-authored output. Dispatch records and points to them, but scripts must avoid printing or writing secrets there unless a later roadmap item explicitly adds content rewriting for script-owned output.

## Planned Next

- Complete remaining `6.8` diagnostics migration.
- Implement PowerShell module wrapper.
- Implement source install and local packaging.
- Implement script secret handoff in staged slices: plan/dry-run validation first, then safe transport-specific parameter binding.

## Deferred Or Out Of Scope For V1

- Full Ansible compatibility.
- Permanent endpoint agent.
- MSI installer.
- Azure Artifacts publishing.
- Enterprise package feeds.
- Managed execution as default.
- Dispatch-owned Blob/media payload staging.
- Command-line script secret values in any form.
