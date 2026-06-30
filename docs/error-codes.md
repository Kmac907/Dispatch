# Error Codes

Dispatch has two related error surfaces:

- CLI process exit codes.
- Per-target failure categories in result JSON.

## CLI Exit Codes

Stable v1 exit-code contract:

| Code | Meaning |
| --- | --- |
| `0` | Success. |
| `1` | Usage, config, inventory, YAML, or planning validation error. |
| `2` | One or more hosts failed. |
| `3` | One or more hosts were unreachable. |
| `4` | Authentication or authorization failure. |
| `5` | Transport initialization failure. |
| `6` | Cancelled. |
| `7` | Plan/check policy failure. |
| `10` | Internal error. |

Current `dispatch run` execution results use this stable mapping after endpoint execution completes. Completed `dispatch apply` execution preserves the same stable underlying run exit codes for executed `ps`, `cmd`, and `exe` tasks. Completed `dispatch push` transfer/execute results and `dispatch hosts test` endpoint-probe results map target failure categories to the same stable process exit-code contract. Usage, configuration, inventory, YAML, planning, and local lifecycle/inspection command failures still return `1`. Policy failures, including current `dispatch run` LocalSystem policy failures and `dispatch run`/`dispatch apply` missing PsExec fallback approval, return `7` before planning or endpoint work. After this exit-code alignment slice, the remaining Roadmap `6.7` work is redaction validation. Automation should prefer `Admin\results.json` for run/apply/push outcomes and structured `hosts test` output for endpoint-probe outcomes.

## Target Failure Categories

Stable categories include:

- `TargetResolutionFailed`
- `ProbeFailed`
- `PayloadPreparationFailed`
- `ScriptTransferFailed`
- `SecretHandoffFailed`
- `ExecutionFailed`
- `UnexpectedExitCode`
- `TimedOut`
- `ArtifactCollectionFailed`
- `CleanupFailed`
- `Cancelled`
- `TransportUnavailable`
- `AuthenticationFailed`
- `AuthorizationFailed`
- `InternalError`

Transport-specific details belong in `transportMetadata`, not in new ad-hoc failure category names.
