# FAQ

## Why did Dispatch choose this transport?

Transport comes from explicit CLI options, job files, inventory metadata, global config, then built-in defaults. Dispatch should not silently fall back to another transport.

## Why did plan pass but run fail?

Planning validates local inputs and supported combinations. The actual run can still fail because endpoints are offline, remoting is disabled, policy blocks access, credentials fail, or a script returns an unexpected exit code.

## What is the difference between run, apply, and push?

`dispatch run` is execution-first: Dispatch runs a script, command, or executable and owns the run lifecycle, events, results, stdout/stderr, and artifacts. `dispatch apply` is declaration-first: it runs supported YAML job tasks in order so repeatable work can live in source control. `dispatch push` is transfer-first: it copies files to a remote destination, and `--execute` is an optional follow-up for a pushed single `.ps1` file.

Use `run ps` for normal ad-hoc script execution. Use `apply` for repeatable job files. Use `push` when the remote file placement is the primary outcome.

## Why can't I pass a password directly?

Command-line secrets are visible to process inspection, shell history, logs, and automation wrappers. Dispatch uses credential references and provider-backed resolution instead.

## Is `--credential` how I pass a secret to my script?

No. `--credential <name>` selects the endpoint credential for transport authentication. Planned script secrets use `dispatch run ps ... --secret name=reference`, which defaults to a protected temporary secret file under the remote run root `secrets\` folder. Plan output may show only redacted file paths, never secret values.

## Why did target selection not match what I expected?

Check inventory groups, host names, selectors, excludes, and de-duplication. Use `hosts list`, `hosts validate`, or `--plan` where available to inspect the resolved set before execution.

## Why was a host skipped?

A host can be skipped or fail early during resolution, probe, payload preparation, credential handoff, or transport setup. Check `Admin\results.json`, `Admin\events.ndjson`, and the target failure category.

## Why do I have stdout but no artifacts?

`stdout.txt` and `stderr.txt` are captured process streams. Artifacts are files created by the script and copied back. If the script did not create `logs\` or `artifacts\`, artifact status can be `not-found`.

## Does Dispatch configure WinRM or firewall rules?

No. Dispatch reports endpoint readiness and failures. Endpoint services and policy remain operator-owned.
