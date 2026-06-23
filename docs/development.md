# Development

## Build

```powershell
dotnet build .\Dispatch.sln
```

## Test

```powershell
dotnet test .\Dispatch.sln
```

Use focused filters for narrow work, then run the full suite before committing shared behavior.

## Run From Source

```powershell
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- --help
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- doctor
```

## Local Validation Flow

1. Run focused tests for the touched behavior.
2. Run `dotnet test .\Dispatch.sln`.
3. For endpoint-execution changes, run least-invasive live validation against at least one approved reachable Windows endpoint.
4. Inspect output files under `C:\ProgramData\Dispatch\Runs`.

## Editing Expectations

- Keep changes scoped.
- Update docs and examples with user-facing behavior changes.
- Do not commit local validation host names.
- Do not commit `workflow/` local tracker files.
- Do not commit secrets or endpoint-specific data.

## Roadmap And Tracker

- `docs/plan.md` is the authoritative roadmap.
- `workflow/build/implementation-plan.md` is ignored local execution state.
- `docs/wiki.md` is the documentation inventory.

The public GitHub Pages site should expose operator and maintainer docs, not the local execution tracker.
