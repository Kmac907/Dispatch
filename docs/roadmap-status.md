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
- `dispatch init config|hosts|job|all` starter YAML scaffolding.
- Logs commands over local run history.
- Credential references with prompt, DPAPI file, Windows Credential Manager, and Azure Key Vault behavior for PSRP and raw WinRM.

## Partial

- `doctor` exists but the full v1 transport-aware diagnostics surface is still expanding.
- `push` supports file and recursive directory transfer over raw WinRM or PSRP, including `--transport auto` selection through inventory/config/default transport policy, `--backup` pre-replacement backup, `--checksum` push-result SHA-256 enforcement/reporting, single-file `.ps1` execute-after-copy through `--execute`, and post-execute script removal through `--cleanup`. Directory cleanup and PsExec push remain deferred unless their boundaries are explicitly reopened.
- `hosts` command group is registered/planned but not fully implemented.
- Inventory support covers the v1 subset needed by current run paths.
- YAML task execution beyond `ps`/`cmd`/`exe`, including real `copy` transfer execution, remains planned.
- Credential handoff is not complete for every entry point; PSCredential wrapper handoff remains planned. PsExec explicit password handoff remains intentionally unsupported under the current no-plaintext PsExec boundary.

## Planned Next

- Continue `6.6` push, hosts, and transport-aware doctor command surfaces.
- Implement `6.7` CLI safety, policy, and stable exit codes.
- Implement `6.8` diagnostics migration.
- Implement PowerShell module wrapper.
- Implement source install and local packaging.

## Deferred Or Out Of Scope For V1

- Full Ansible compatibility.
- Permanent endpoint agent.
- MSI installer.
- Azure Artifacts publishing.
- Enterprise package feeds.
- Managed execution as default.
- Dispatch-owned Blob/media payload staging.
