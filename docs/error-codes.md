# Error Codes

Dispatch has two related error surfaces:

- CLI process exit codes.
- Per-target failure categories in result JSON.

## CLI Exit Codes

Planned v1 stable exit codes:

| Code | Meaning |
| --- | --- |
| `0` | Success. |
| `1` | Usage, config, inventory, or YAML validation error. |
| `2` | One or more hosts failed. |
| `3` | One or more hosts were unreachable. |
| `4` | Authentication or authorization failure. |
| `5` | Transport initialization failure. |
| `6` | Cancelled. |
| `7` | Plan/check policy failure. |
| `10` | Internal error. |

Current commands may not yet return the full planned v1 mapping everywhere. Automation should prefer `Admin\results.json` for detailed per-target outcomes.

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
