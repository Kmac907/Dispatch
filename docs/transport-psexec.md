# PsExec Transport

The PsExec transport uses SMB/admin-share staging plus PsExec process execution from the admin workstation.

## Endpoint Requirements

- Windows target.
- Local administrator rights on the target.
- `\\<target>\C$` admin share reachable.
- File and printer sharing / SMB allowed by network and firewall policy.
- PsExec executable available on the admin workstation.
- Endpoint security tooling permits PsExec behavior.

## Execution Context

V1 supports current admin context and explicit system execution where policy permits it.

LocalSystem execution must be explicit through `dispatch run ... --system` and the selected config must set `dispatch.allow_run_as_system: true`. Without that config approval, Dispatch returns policy exit code `7` before planning or endpoint work. Dispatch does not silently elevate or switch contexts.

PsExec fallback is separately policy-gated. Omitted `--transport` and `--transport auto` must not implicitly select PsExec unless fallback policy is approved. Explicit `--transport psexec` remains the CLI opt-in. Config approval is `dispatch.allow_psexec_fallback: true`; inventory approval can be `allow_psexec_fallback: true` on defaults, group vars, host entries, or host vars where supported by the implementation. Without fallback approval, Dispatch returns policy exit code `7` before planning or endpoint work.

## Current Boundaries

- No plaintext `psexec -u/-p` style password passing.
- No Dispatch-managed command-line password handoff.
- No automatic admin-share or firewall remediation.
- PsExec live validation may be blocked in environments where admin-share staging is disabled.

## Common Failures

| Symptom | Likely cause |
| --- | --- |
| DNS/name resolution failure | Target name cannot resolve from the admin workstation. |
| Admin share inaccessible | `C$` disabled, blocked, or caller lacks local admin rights. |
| Access denied | Rights, UAC remote restrictions, EDR, or endpoint policy. |
| PsExec missing | PsExec is not installed or not on the configured path. |

## Example

```powershell
dispatch run ps .\Fix.ps1 --target PC001 --transport psexec
dispatch run ps .\Fix.ps1 --target PC001 --transport psexec --system
```
