# Script-Owned Payloads

Dispatch v1 prepares and runs the selected PowerShell script. It does not stage installer media, package payloads, Azure Blob content, Azure Files content, or SMB/HTTPS payloads for the script.

Scripts own their external payload retrieval. If a script needs an MSI, ZIP, configuration file, or other payload, pass ordinary non-secret values as script arguments and let the script download or copy the payload.

Examples of script-owned inputs:

```powershell
dispatch run --dry-run `
  --script .\Install-App.ps1 `
  --computer-name PC001 `
  -- -PackageUri https://contoso.example/packages/app.msi -Version 1.2.3
```

```powershell
dispatch run --dry-run `
  --script .\Install-App.ps1 `
  --target-file .\targets.txt `
  -- -SourcePath \\fileserver\packages\app.msi
```

v1 guardrails:

- Dispatch copies only the selected script to the target-local Dispatch run path.
- Dispatch passes ordinary script arguments through unchanged.
- Dispatch logs and result metadata should describe orchestration behavior, not payload download internals.
- Dispatch does not generate, validate, refresh, store, or pass SAS tokens.
- Do not pass credentials, SAS tokens, or other secrets on the command line.
- Post-MVP secret handoff must use a redacted protected secret-file model rather than raw command-line values.
