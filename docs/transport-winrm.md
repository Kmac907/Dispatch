# Raw WinRM Transport

The raw WinRM transport uses WS-Management shell/command semantics rather than PowerShell SDK runspaces.

## Requirements

- WinRM listener reachable on the target.
- WS-Management shell access permitted.
- Selected authentication mechanism allowed by endpoint policy.
- HTTPS listener for Basic authentication if Basic is ever enabled.
- Caller or resolved credential has rights to create shells and run commands.

## Behavior

Raw WinRM supports:

- Direct command execution.
- PowerShell script execution by invoking `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <prepared script>`.
- Chunked script upload without SMB/admin shares.
- Artifact collection over the WinRM channel.
- Process stdout/stderr and exit code capture.

## Differences From PSRP

Raw WinRM captures process stdout/stderr. It does not provide rich PowerShell streams such as warning, verbose, debug, and information as native stream records.

## Validation Commands

```powershell
dispatch run cmd whoami --target PC001 --transport winrm --no-progress --output json
dispatch run ps .\Smoke.ps1 --target PC001 --transport winrm --no-progress --output json
```

## Common Failures

| Failure | Meaning |
| --- | --- |
| `TransportUnavailable` | WinRM endpoint or shell service is unavailable. |
| `AuthenticationFailed` | Authentication failed before execution. |
| `AuthorizationFailed` | Authenticated user is not allowed to create shell/execute. |
| `TimedOut` | Probe, shell, command, upload, or artifact operation timed out. |

Dispatch does not run `winrm quickconfig` or change endpoint policy.
