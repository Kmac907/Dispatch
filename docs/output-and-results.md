# Output And Results

Dispatch writes one run folder per execution under the configured local run root.

## Default local layout

```text
C:\ProgramData\Dispatch\Runs\<RunId>\
  Admin\
    events.ndjson
    results.json
  Targets\
    <Target>\
      stdout.txt
      stderr.txt
      artifacts\...
```

Default files:

- `Admin\events.ndjson`
  - the canonical structured run log
  - contains run start, plan, execution start, target progress, final target results, and final run summary
- `Admin\results.json`
  - the compact final run summary for automation and operator review
- `Targets\<Target>\stdout.txt`
  - captured process stdout for that target
- `Targets\<Target>\stderr.txt`
  - captured process stderr for that target
- `Targets\<Target>\artifacts\...`
  - copied-back script-created files when artifact collection finds them

## Optional local files

These are supported by result policy but are not written by default:

- `Admin\results.csv`
- `Admin\dispatch.log`
- `Targets\<Target>\result.json`

If those optional files are disabled, the final run summary still exists in `Admin\results.json`, and per-target `resultPath` values in that summary are empty.

## Canonical event stream

`Admin\events.ndjson` is the source of truth for the run.

It records:

- `run.started`
- `plan`
- `execution.started`
- `progress`
- `target.result`
- `result`

Use this file when you need:

- durable replay of a run
- machine ingestion
- a stable event history for `dispatch logs tail`, `dispatch logs export`, and `dispatch logs retry`

## Summary file

`Admin\results.json` is the reduced final view.

It contains:

- run metadata
- target summaries
- counts
- output/artifact paths where applicable

Use this file for:

- automation that wants the final run outcome
- operators who need the final state without replaying the full event stream
- `dispatch logs list`, which enumerates local run history from the reduced summaries

## Final summary file locations

The rich/table completion summary exposes an `Outputs` panel after the target outcome table:

- `Admin\results.json`
- `Admin\events.ndjson`
- `Targets\<Target>\stdout.txt`
- `Targets\<Target>\stderr.txt`

It also shows the per-target local root pattern plus separate stdout/stderr lines so an operator can move from the completion summary to the durable files without reconstructing the run layout by hand.

## Console output modes

- `rich`
  - Spectre operator UI
- `table`
  - stable human-readable table/text output
- `json`
  - one JSON document
- `ndjson`
  - one stdout event per line for the active command path
- `yaml`
  - one YAML document

Stdout NDJSON and durable `events.ndjson` are related but different:

- stdout NDJSON is the live command-output stream
- `Admin\events.ndjson` is the durable local run-history file

The current local log-inspection surface reads those same files:

- `dispatch logs list` enumerates local runs from `Admin\results.json`
- `dispatch logs show latest` reads the latest local `Admin\results.json` and exposes the same final-summary/output-location view as the original run result
- `dispatch logs tail [run-id|latest] --count <n>` reads the canonical `Admin\events.ndjson` stream for the selected run and returns the last `n` durable events, defaulting to `20`
- `dispatch logs export [run-id|latest] --dest <path>` writes a per-run export folder under the destination with `results.json`, `events.ndjson` when present, and `results.csv`
- `dispatch logs retry [run-id|latest]` reads `Admin\results.json` and emits a read-only retry plan for failed, timed-out, and cancelled targets; it includes a manual `dispatch run cmd ...` suggestion only when the original command payload can be reconstructed from the final summary

`logs retry` does not automatically re-execute endpoints in v1. Script retries are reported as not reconstructible from `results.json` because the final summary intentionally does not persist the original script path or script arguments.

## Live dashboard progress contract

The live dashboard shows progress only when Dispatch has a real denominator.

Current measurable per-target cases are:

- WinRM script upload:
  - chunk count progress
  - uploaded bytes when the shell transfer reports them
- WinRM artifact download:
  - downloaded bytes when the remote archive size is known

If a phase does not expose a real denominator, Dispatch shows phase/status/elapsed information instead of a fake percentage.

## Remote endpoint layout

Dispatch still uses the endpoint run root for script execution and script-owned files:

```text
C:\ProgramData\Dispatch\Runs\<RunId>\
  script\
  logs\
  artifacts\
```

Dispatch prepares scripts under `script\` when the selected transport requires an endpoint-local script path.

Scripts may write retrievable files under:

- `logs\`
- `artifacts\`

or under declared artifact paths for the run.

## Stdout/stderr vs artifacts

These are different concerns:

- `stdout.txt` / `stderr.txt`
  - process output captured by Dispatch
- `artifacts\...`
  - files the script created intentionally and Dispatch copied back

A script can succeed and still produce no copied-back artifacts.

## Artifact defaults and statuses

Default artifact folders:

- `logs`
- `artifacts`

Artifact collection status values:

- `collected`
  - declared/default artifact paths were found and copied back
- `not-found`
  - the script succeeded, but the declared/default artifact paths were not present
- `failed`
  - artifact collection itself failed after Dispatch tried to retrieve the files
- `skipped`
  - Dispatch never reached artifact collection because an earlier phase failed

## What automation should read

Preferred order:

1. `Admin\results.json` for final outcome
2. `Admin\events.ndjson` for durable event history
3. `Targets\<Target>\stdout.txt` and `stderr.txt` for raw target output
4. copied-back artifacts for script-owned files
