# Transports

Dispatch exposes endpoint protocols as explicit transports:

```powershell
--transport psrp
--transport winrm
--transport psexec
```

Each transport implements the same core model: probe, prepare, execute, collect artifacts, cleanup, result classification, and metadata.

## Selection Guide

| Transport | Use when | Tradeoff |
| --- | --- | --- |
| `psrp` | You want PowerShell remoting semantics, streams, and credential handoff support. | Requires PowerShell remoting and permitted session configuration. |
| `winrm` | You want raw WS-Management shell/command execution and credential handoff without SMB staging. | Process stdout/stderr only; no rich PowerShell stream model. |
| `psexec` | You need PsExec behavior and SMB/admin shares are available. | Depends on admin shares, PsExec binary/policy, and endpoint security controls. |

## Current Implementation State

- PSRP: implemented for command/script execution, stream records, artifacts, and current credential providers.
- Raw WinRM: implemented for command/script execution, remote upload, artifacts, current credential providers, and timeout/failure classification.
- PsExec: implemented for direct execution plumbing and probes, but further PsExec-first expansion depends on environments where admin-share staging is available.

## Shared Requirements

- Windows target endpoint.
- The selected protocol must be enabled and reachable.
- The caller or resolved credential must have sufficient rights.
- Dispatch does not silently remediate endpoint configuration.

## No Silent Fallback

Dispatch does not silently fall back from one transport to another. Transport decisions must be explicit through CLI, job, inventory, or config.
