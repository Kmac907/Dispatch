# Architecture

Dispatch is split into core orchestration, transport implementations, CLI rendering, and tests.

## Project Layout

```text
src/
  Dispatch.Core/
  Dispatch.Cli/
  Dispatch.Transports.PsExec/
  Dispatch.Transports.Psrp/
  Dispatch.Transports.WinRm/
tests/
  Dispatch.Core.Tests/
  Dispatch.Cli.Tests/
docs/
module/
packaging/
workflow/
```

## Core Responsibilities

`Dispatch.Core` owns:

- request and job models
- target resolution
- planning
- result models
- local run layout
- event contracts
- orchestration
- artifact abstractions
- transport interfaces

## Planner / Executor Split

The planner validates local inputs and builds an execution plan before endpoint work starts.

The executor consumes the plan, creates the run folder, emits events, runs target workers, calls transports, captures output, collects artifacts, and writes summaries.

## Target Resolver

The target resolver normalizes direct targets, target files, and inventory selectors into deterministic target lists.

## Transport Abstraction

Transports implement common phases:

- probe
- prepare payload
- execute script
- execute command
- collect artifacts
- cleanup

Expected endpoint failures should return structured results rather than escaping as unclassified exceptions.

## Renderer And Output Model

CLI rendering is owned by `Dispatch.Cli`. Worker threads do not write directly to the console. Rich live rendering consumes events and heartbeat updates; structured output modes are automation contracts.

## Dependency Boundaries

- Transport projects depend on core contracts.
- CLI depends on core plus registered transports.
- Core does not depend on Spectre.Console.
- Tests cover contracts, planning, rendering, transport behavior, and serialization.
