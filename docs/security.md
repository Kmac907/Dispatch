# Security

Dispatch is designed to avoid plaintext secret handling in command lines, YAML files, logs, and result files.

## No Plaintext Credentials

Do not store passwords in:

- `config.yml`
- `hosts.yml`
- `job.yml`
- command-line arguments
- scripts committed with jobs
- logs or artifacts

Rejected YAML keys include secret-like fields such as `password`, `secret`, `token`, and `sas`.

## Credential References

Credential references are names such as `prod-admin`. References may appear in jobs, inventories, CLI overrides, plans, and logs.

Resolved passwords are kept in memory only and are not serialized.

## No Command-Line Secret Handoff

Dispatch does not support passing endpoint passwords, SAS tokens, or secret values directly as command-line flags.

## Script Secret Handoff

Endpoint credentials and script secrets are separate. `--credential <name>` chooses the remoting credential; it does not pass a secret to the script.

The planned script secret CLI is `dispatch run ps ... --secret name=reference`. The default handoff is a protected temporary secret file under the remote run root `secrets\` folder. Plan and dry-run output can validate references and render redacted secret-file paths, but real protected remote staging and cleanup are later implementation work.

## Logging And Redaction

Secret values must not appear in:

- rich console output
- JSON, NDJSON, or YAML structured output
- durable run files
- stdout/stderr when controlled by Dispatch
- dry-run or plan output
- transport command rendering

Scripts are still responsible for not writing secrets to their own stdout/stderr, logs, or artifacts.

## Transport Risk Notes

PSRP and WinRM depend on endpoint remoting policy. PsExec depends on SMB/admin shares and endpoint security policy. Dispatch reports readiness and failures but does not silently change endpoint policy.

## Admin Context

Remote execution uses the current admin context or a configured credential reference, depending on transport support and selected provider. LocalSystem or other elevated endpoint contexts must be explicit and policy-approved where implemented.

## Script-Owned Payload Boundary

Scripts own external payload retrieval. Dispatch does not download installer media or Blob payloads in v1. If scripts use secrets to retrieve payloads, those secrets must be passed through a redacted, protected mechanism when that feature is implemented.
