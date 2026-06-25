# Concepts

Dispatch uses a small set of concepts consistently across ad-hoc commands, YAML jobs, transports, logs, and results.

## Job

A job is one Dispatch execution request. It contains targets, payload, transport, execution context, expected exit codes, artifact rules, and result policy.

## Target

A target is one Windows endpoint selected for a job. Each target has its own state, transport metadata, stdout/stderr files, result fields, and artifact collection status.

## Inventory

An inventory is a file that defines hosts, groups, defaults, and optional per-host or per-group credential/transport metadata. Dispatch supports simple host files and YAML inventories.

## Selector

A selector chooses which inventory hosts to run against. Selectors may name hosts or groups. Exclude selectors remove matching hosts from the final target list.

## Transport

A transport is the endpoint protocol Dispatch uses for execution. V1 transports are:

- `psrp`
- `winrm`
- `psexec`

Each transport maps into the same job, target, result, output, and artifact model.

## Plan

A plan is the validated execution shape before endpoint work starts. Planning validates local inputs, target selection, supported transport/payload combinations, output paths, expected exit codes, and credential references where possible.

## Run

A run is the actual execution of a plan. A run creates a local run folder, emits events, updates target states, captures output, collects artifacts, and writes a final summary.

## Command Intent

The main command groups map to different operator intents:

- `run` is execution-first. Use it when Dispatch should run a script, command, or executable as a managed execution and collect the result.
- `apply` is declaration-first. Use it when work should live in a YAML job with ordered tasks, target policy, tags, and repeatable review.
- `push` is transfer-first. Use it when the important outcome is copying a file or directory to an exact remote path. `push --execute` can run a pushed single `.ps1`, but the command still owns file placement rather than the full job authoring model.
- `logs` is history-first. Use it after a run to inspect local result files without contacting endpoints.
- `creds` is reference-first. Use it to manage provider-backed credential state selected by config, inventory, jobs, or CLI options.
- `hosts` is inventory-first. Use it to inspect and validate host data before execution once the planned host command group is implemented.

## Result

A result is the final machine-readable outcome. `Admin\results.json` contains the reduced run summary and each target result.

## Artifact

An artifact is a file created by the script and copied back by Dispatch. Artifacts are different from process stdout/stderr.

## Log

There are two log concepts:

- Dispatch run event logs, especially `Admin\events.ndjson`.
- Script-created files under remote `logs\` or declared artifact folders.

## Direct Execution

Direct execution runs the selected script or command through the selected transport without generating an endpoint-side harness. Direct execution is the v1 default.

## Managed Execution

Managed execution is a planned later mode where Dispatch would generate a small harness for stronger result normalization. It is not the v1 default.

## Captured Output vs Script-Created Files

Dispatch always captures process stdout/stderr into:

```text
Targets\<target>\stdout.txt
Targets\<target>\stderr.txt
```

Scripts may also create files under remote `logs\` or `artifacts\`. Those files are copied back only when present and when artifact collection succeeds.
