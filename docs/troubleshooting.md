# Troubleshooting

Start with:

```powershell
dispatch doctor
dispatch logs show latest
dispatch logs tail latest --count 50
```

## Target Resolution Failures

Common causes:

- Misspelled host name.
- DNS cannot resolve from the admin workstation.
- Selector references a group or host not present in the inventory.
- Exclude selector removed every target.

Fix the inventory or selector before retrying.

## Inventory Validation Failures

Common causes:

- Unsupported top-level YAML section.
- Unsupported field under `hosts`, `groups`, or `vars`.
- Plaintext secret-like key such as `password`, `secret`, `token`, or `sas`.
- Conflicting inherited group metadata.

Inventory validation fails before endpoint work starts.

## PsExec Failures

Common causes:

- PsExec missing or blocked by policy.
- Admin share unavailable.
- Caller lacks local administrator rights.
- Endpoint firewall or security tooling blocks PsExec behavior.

## WinRM Failures

Common causes:

- WinRM listener not reachable.
- Shell creation denied.
- Authentication or authorization policy blocks the caller.
- HTTPS required for selected authentication.

## PSRP Failures

Common causes:

- PowerShell remoting disabled.
- Session configuration missing.
- User lacks rights to enter the selected configuration.
- Unsupported future auth/connection mode selected.

## Config Errors

Common causes:

- Invalid YAML.
- `--config` path does not exist.
- Credential reference not present in the loaded config.
- Provider required metadata missing.
- Key Vault URI is not HTTPS.

## What Doctor Proves

`dispatch doctor` proves local prerequisites for the selected transport scope. Use `--transport psexec`, `--transport psrp`, or `--transport winrm` when a failure only matters for one remoting path. Omitted `--transport` and `--transport auto` check the current local transport prerequisite set. Current shared checks include runtime, shell, global Dispatch config parseability, output path, run-history layout, user context, and local policy state. The config check reports the global config path as absent, parseable, inaccessible, or invalid; malformed YAML/JSON and plaintext secret fields are invalid config failures. The PsExec scope also checks configured PsExec path, effective PsExec policy, current-user PsExec EULA registry state, and local admin-token state; EULA is read-only and reported as a warning when missing. Use `--output json`, `--output ndjson`, or `--output yaml` when automation needs a machine-readable report. It does not prove that every endpoint can execute a job, and it does not remediate endpoint settings.

## Script Succeeded But Artifacts Are Missing

A script can succeed and artifact collection can report `not-found` when the script did not create the expected `logs\` or `artifacts\` folders. This is not a script failure.

`stdout.txt` and `stderr.txt` can exist even when no script-created logs or artifacts were copied back, because stdout/stderr are captured process streams.

## Artifact Collection Failed

Artifact collection can fail after execution succeeded. Treat this separately:

- execution result tells whether the script or command succeeded
- artifact status tells whether script-created files were copied back
