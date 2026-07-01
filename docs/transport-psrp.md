# PSRP Transport

The PSRP transport uses PowerShell SDK remote runspaces over WSMan.

## Requirements

- PowerShell remoting enabled on the target.
- WinRM listener reachable.
- Session configuration exists and permits the caller.
- Caller or resolved credential has rights for the selected session configuration.

Default configuration name:

```text
Microsoft.PowerShell
```

## Behavior

PSRP supports:

- Direct command execution.
- PowerShell script execution.
- PowerShell stream capture into optional `streamRecords`.
- Artifact collection over the remoting channel.
- Current-user `Default` or `Negotiate` authentication in the current implementation.
- Credential resolution for supported providers when running PSRP.

## Differences From Raw WinRM

PSRP runs through PowerShell remoting runspaces. It can capture warning, verbose, debug, information, and error streams as structured records where practical.

Raw WinRM runs shell commands and primarily captures process stdout/stderr.

## Credential Use

Current PSRP credential resolution supports:

- `prompt`
- `dpapi_file`
- `windows_credential_manager`
- `azure_keyvault`
- `pscredential` when launched through the PowerShell module's protected PSCredential handoff.

## Common Failures

| Symptom | Likely cause |
| --- | --- |
| Endpoint unreachable | DNS, firewall, WinRM listener, or network issue. |
| Authorization failed | User cannot enter the selected session configuration. |
| Configuration not found | Requested PowerShell session configuration is unavailable. |
| Authentication unsupported | Requested future auth mode is modeled but not implemented yet. |
