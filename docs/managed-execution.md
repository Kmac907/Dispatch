# Managed Execution

Managed execution is not part of current v1 scope. This page exists because the documentation inventory reserves it for a later feature.

## Planned Purpose

Managed execution would generate a minimal internal harness only when explicitly selected. The harness would normalize stdout/stderr, timeout behavior, child process exit classification, and result files across transports.

## Boundary

Direct execution remains the v1 default. Managed execution must not rewrite user scripts and must not replace direct execution.

## Planned Option

```powershell
dispatch run ps .\Fix.ps1 --execution-mode managed
```

The exact option shape may change when the feature is implemented.
