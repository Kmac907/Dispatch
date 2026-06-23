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
- `dispatch apply <job.yml> --plan` and `dispatch apply <job.yml> --check` for selected multi-task script-first `ps` plans, plus execution for one selected `ps` task.
- Logs commands over local run history.
- Credential references with prompt, DPAPI file, Windows Credential Manager, and Azure Key Vault behavior for PSRP and raw WinRM.

## Partial

- `doctor` exists but the full v1 transport-aware diagnostics surface is still expanding.
- `hosts`, `push`, and `init` command groups are registered/planned but not fully implemented.
- Inventory support covers the v1 subset needed by current run paths.
- Multi-task YAML execution and non-`ps` task types remain planned.
- Credential handoff is not complete for every entry point; PSCredential wrapper handoff remains planned. PsExec explicit password handoff remains intentionally unsupported under the current no-plaintext PsExec boundary.

## Planned Next

- Continue `6.5` YAML apply/job model.
- Implement `6.6` push, hosts, doctor, and init command surfaces.
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
