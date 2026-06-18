# Dispatch Documentation Inventory

This file is the full list of required documentation for Dispatch once the tool is complete. It is intended to act as the documentation build list for the `docs/` tree.

The goal is that a new operator, engineer, or maintainer can pick up Dispatch without tribal knowledge.

## Documentation goals

- Make the tool understandable to a new user from zero.
- Make the current behavior and boundaries explicit.
- Separate operator guidance from maintainer guidance.
- Keep schema, command, and result contracts exact.
- Prevent roadmap gaps from turning into undocumented behavior.

## Core documentation

### 1. `docs/README.md`

Purpose:
- Primary docs entry point.

Must answer:
- What Dispatch is
- Who it is for
- What problem it solves
- Current status
- Where to start next

### 2. `docs/getting-started.md`

Purpose:
- Fastest path to first successful use.

Must include:
- prerequisites
- build/run basics
- one local validation command
- one first real `run ps` example
- where results and logs go

### 3. `docs/installation.md`

Purpose:
- Installation and setup guide.

Must include:
- run from source
- packaged install path when implemented
- PowerShell module install path when implemented
- upgrade path
- uninstall/removal basics

### 4. `docs/concepts.md`

Purpose:
- Define the mental model.

Must define:
- job
- target
- inventory
- selector
- transport
- plan
- run
- result
- artifact
- log
- direct execution vs managed execution
- the distinction between Dispatch-captured process output and script-created files

### 5. `docs/command-reference.md`

Purpose:
- Full CLI reference.

Must include:
- command tree
- arguments
- options
- defaults
- examples
- implemented vs planned surfaces clearly marked

## Operator documentation

### 6. `docs/running-scripts.md`

Purpose:
- Primary operator workflow for `dispatch run ps`.

Must include:
- single-host execution
- multi-host execution
- using `--plan`
- using `--output`
- using `--config`
- using expected exit codes
- examples that are safe and realistic
- how scripts should write retrievable files into the remote run folder
- recommended use of remote `logs\` and `artifacts\` folders
- what happens when a script only writes to stdout/stderr and does not create artifact folders

### 7. `docs/inventory.md`

Purpose:
- Explain how target inventories work.

Must include:
- text inventories
- YAML inventories
- target selectors
- exclude selectors
- deterministic ordering behavior
- what is currently supported vs not supported

### 8. `docs/inventory-schema.md`

Purpose:
- Exact contract for the inventory format.

Must include:
- supported top-level sections
- supported fields
- supported syntax forms
- valid examples
- invalid examples
- precedence rules that apply today
- validation failure behavior
- explicit boundary with later roadmap items

### 9. `docs/transports.md`

Purpose:
- Transport overview and selection guide.

Must include:
- what each transport is
- current implementation state
- when to use each transport
- requirements
- tradeoffs
- current blockers and limitations

### 10. `docs/transport-psexec.md`

Purpose:
- PsExec-specific operator guidance.

Must include:
- endpoint requirements
- admin-share dependency
- current-context vs SYSTEM behavior
- common failures
- known limitations

### 11. `docs/transport-winrm.md`

Purpose:
- Raw WinRM operator guidance.

Must include:
- listener requirements
- authentication expectations
- policy prerequisites
- common failures
- example validation commands

### 12. `docs/transport-psrp.md`

Purpose:
- PSRP-specific operator guidance.

Must include:
- remoting/session requirements
- configuration name expectations
- differences from raw WinRM
- common failures

### 13. `docs/configuration.md`

Purpose:
- Dispatch config reference.

Must include:
- supported config keys
- config file location/shape
- precedence
- explicit `--config` behavior
- ambient config behavior
- examples

### 14. `docs/output-and-results.md`

Purpose:
- Explain outputs, results, and how to consume them.

Must include:
- console output modes
- JSON / NDJSON / YAML output behavior
- run/result file layout
- CSV/JSON summaries
- canonical `Admin\\events.ndjson` behavior
- reduced `Admin\\results.json` behavior
- default vs optional local output files
- artifact location
- what automation should read
- the local run folder layout vs the remote endpoint run folder layout
- the difference between `stdout.txt` / `stderr.txt` and copied-back script-created files
- the default artifact folders: `logs` and `artifacts`
- what artifact status values mean, including `collected`, `not-found`, `failed`, and `skipped`

### 15. `docs/troubleshooting.md`

Purpose:
- Primary operator troubleshooting guide.

Must include:
- target resolution failures
- inventory validation failures
- PsExec failures
- WinRM failures
- PSRP failures
- config errors
- common error messages
- what `dispatch doctor` proves and does not prove
- why a script can succeed while artifact collection is `not-found` or `failed`
- why `stdout.txt` may exist even when no copied-back script logs/artifacts exist

### 16. `docs/security.md`

Purpose:
- Explain the security model and sharp edges.

Must include:
- no plaintext credential policy
- no command-line secret handoff policy
- logging/redaction behavior
- script-owned payload boundary
- transport-specific risk notes
- admin-context implications

### 17. `docs/script-owned-payloads.md`

Purpose:
- Explain what Dispatch stages and what scripts own.

Required status:
- already exists

Must continue to cover:
- payload boundary
- installer/media ownership
- script arguments
- secret handling expectations
- cross-reference where script-created logs and artifacts should be written, but do not make this the primary output/results doc

## Reference and contract documentation

### 18. `docs/error-codes.md`

Purpose:
- Stable exit-code and failure-category reference.

Must include:
- CLI process exit codes
- target failure categories
- operational meaning of each code/category

### 19. `docs/result-schema.md`

Purpose:
- Machine-readable result contract.

Must include:
- run result schema
- per-target result schema
- artifact references
- compatibility/stability expectations

### 20. `docs/examples.md`

Purpose:
- Copy/paste examples for common use.

Must include:
- simple `run ps`
- inventory examples
- config examples
- output mode examples
- transport examples when implemented

### 21. `docs/faq.md`

Purpose:
- Short answers to common user questions.

Good questions to cover:
- why a transport was chosen
- why plan passed but run failed
- why secrets cannot be passed directly
- why target selection did not match
- why a host was skipped or excluded

## Developer and maintainer documentation

### 22. `docs/architecture.md`

Purpose:
- Internal architecture guide for contributors.

Must include:
- project layout
- planner/executor split
- target resolver
- transport abstraction
- renderer/output model
- current dependency boundaries

### 23. `docs/development.md`

Purpose:
- Contributor setup and workflow.

Must include:
- build commands
- test commands
- local validation flow
- coding/editing expectations
- how roadmap/tracker files are used

### 24. `docs/testing-and-validation.md`

Purpose:
- Define how Dispatch is verified.

Must include:
- unit and CLI test expectations
- live validation expectations
- least-invasive live test patterns
- validation host guidance
- how to report environmental blockers accurately

### 25. `docs/roadmap-status.md`

Purpose:
- Human-readable implementation status separate from deep roadmap detail.

Must include:
- what works now
- what is partial
- what is planned next
- what is intentionally deferred

## Required docs for unfinished v1 roadmap features

These are not post-MVP docs. They are still required for Dispatch, but they are naturally gated by the corresponding feature landing in the product. They should be written as those roadmap items become real.

### 26. `docs/jobs.md`

Purpose:
- Operator guide for `apply` / job execution.

### 27. `docs/job-schema.md`

Purpose:
- Exact YAML job contract.

### 28. `docs/credentials.md`

Purpose:
- Credential-reference model and provider behavior.

### 29. `docs/logs.md`

Purpose:
- Durable run-history model and `logs` command behavior.

### 30. `docs/powershell-module.md`

Purpose:
- PowerShell module usage and automation guidance, including the command-aligned wrapper surface over `dispatch.exe` (`Invoke-DispatchPowerShell`, `Invoke-DispatchCommand`, `Invoke-DispatchExecutable`, `Invoke-DispatchJob`, `Test-Dispatch`, and `Get-DispatchVersion`) and the rule that the module does not introduce a separate interactive shell launcher.

### 31. `docs/distribution.md`

Purpose:
- Packaging, source install, bootstrap install, ZIP install, and local installation guidance for the v1 distribution model.

## Docs only required if out-of-scope v1 features are later brought back into scope

These are the only docs in this file that are not part of the current v1 documentation completion set. They map to features that `docs/plan.md` explicitly marks out of scope for v1.

### 32. `docs/managed-execution.md`

Purpose:
- Managed execution mode behavior, result normalization, and when to use it instead of direct execution.

Reason it is not part of current v1 docs:
- `Managed/harness execution mode` is explicitly out of scope for v1 in `docs/plan.md`.

### 33. `docs/enterprise-distribution.md`

Purpose:
- Azure Artifacts, MSI, enterprise package distribution, private feeds, and related install channels beyond the local/source-install v1 path.

Reason it is not part of current v1 docs:
- MSI and Azure Artifacts publishing are explicitly out of scope for v1 in `docs/plan.md`.

## Minimum sane documentation set

If a smaller but still credible initial docs set is needed, these are the minimum high-value documents:

1. `docs/README.md`
2. `docs/getting-started.md`
3. `docs/installation.md`
4. `docs/concepts.md`
5. `docs/command-reference.md`
6. `docs/running-scripts.md`
7. `docs/inventory.md`
8. `docs/inventory-schema.md`
9. `docs/transports.md`
10. `docs/configuration.md`
11. `docs/output-and-results.md`
12. `docs/troubleshooting.md`

## Highest-priority missing docs right now

Based on the current repo state, these are the highest-value docs to create first:

1. `docs/README.md`
2. `docs/getting-started.md`
3. `docs/command-reference.md`
4. `docs/inventory-schema.md`
5. `docs/transports.md`
6. `docs/configuration.md`
7. `docs/output-and-results.md`
8. `docs/troubleshooting.md`
9. `docs/security.md`
10. `docs/architecture.md`

## Notes

- `docs/plan.md` remains the authoritative roadmap, not the operator wiki.
- `docs/cli-design.md` remains the CLI contract/design doc, not the operator quickstart.
- `docs/wiki.md` is the inventory of required docs, not the end-user docs themselves.
- The shared artifact/logging model belongs primarily in `docs/output-and-results.md`, with supporting operator and troubleshooting coverage in `docs/running-scripts.md`, `docs/concepts.md`, and `docs/troubleshooting.md`.
- Do not use `docs/transports.md`, `docs/transport-winrm.md`, or `docs/transport-psexec.md` as the primary home for shared artifact/logging semantics. Those docs should only describe transport-specific behavior and limitations.
- Items under `Required docs for unfinished v1 roadmap features` are still required for Dispatch v1; they are only deferred until the corresponding roadmap feature is implemented.
- Only items under `Docs only required if out-of-scope v1 features are later brought back into scope` are outside the current v1 documentation completion set.
