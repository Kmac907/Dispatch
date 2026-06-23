# Logs

Dispatch logs commands read local run history under the configured run root.

Default root:

```text
C:\ProgramData\Dispatch\Runs
```

## Commands

```powershell
dispatch logs list
dispatch logs show latest
dispatch logs tail latest --count 50
dispatch logs export latest --dest .\exports
dispatch logs retry latest
```

## Run History Files

`logs` commands read:

- `Admin\results.json`
- `Admin\events.ndjson`

They do not require live endpoint access.

## List

`logs list` enumerates local runs and displays reduced summary information.

## Show

`logs show <run-id|latest>` reads the final summary and prints run/target outcomes plus output file locations.

## Tail

`logs tail <run-id|latest>` reads durable NDJSON events and returns the latest events.

## Export

`logs export` copies a selected run's summary/event data to a destination folder. It can include `results.json`, `events.ndjson`, and generated `results.csv` where available.

## Retry

`logs retry` is read-only in v1. It builds a retry plan from failed, timed-out, or cancelled targets. It can suggest a manual command when the original command payload can be reconstructed from `results.json`.

Script retries may be reported as not reconstructible because the final summary intentionally does not persist the original script path and arguments.
