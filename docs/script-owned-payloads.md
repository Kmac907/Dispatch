# Script-Owned Payloads

Dispatch v1 prepares and runs the selected PowerShell script. It does not stage installer media, package payloads, Azure Blob content, Azure Files content, or SMB/HTTPS payloads for the script.

Scripts own their external payload retrieval. If a script needs an MSI, ZIP, configuration file, or other payload, pass ordinary non-secret values as script arguments and let the script download or copy the payload.

Examples of script-owned inputs:

```powershell
dispatch run ps .\Install-App.ps1 `
  --target PC001 `
  --plan `
  -- -PackageUri https://contoso.example/packages/app.msi -Version 1.2.3
```

```powershell
dispatch run ps .\Install-App.ps1 `
  --target-file .\targets.txt `
  --plan `
  -- -SourcePath \\fileserver\packages\app.msi
```

The legacy compatibility shape `dispatch run --script <path> --computer-name <names>` remains supported while module and install callers migrate, but new examples should use either `dispatch run ps <script.ps1>` or the planned `Invoke-DispatchPowerShell` wrapper documented in `docs/cli-design.md`.

v1 guardrails:

- Dispatch copies only the selected script to the target-local Dispatch run path.
- Dispatch passes ordinary script arguments through unchanged.
- Dispatch logs and result metadata should describe orchestration behavior, not payload download internals.
- Dispatch does not generate, validate, refresh, store, or pass SAS tokens.
- Do not pass credentials, SAS tokens, or other secrets on the command line.
- Planned script secret handoff uses `dispatch run ps ... --secret name=reference`, separate from endpoint `--credential`.
- The default handoff is script parameter binding. Plan/dry-run output may render redacted parameter bindings such as `-packageSas [redacted]`, while real safe parameter binding is later work.
