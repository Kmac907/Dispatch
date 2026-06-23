# Running Scripts

`dispatch run ps` is the primary operator workflow for ad-hoc PowerShell script execution.

## Single Host

```powershell
dispatch run ps .\Fix.ps1 --target PC001 --transport psrp
```

## Multiple Hosts

```powershell
dispatch run ps .\Fix.ps1 --target PC001,PC002,PC003 --transport psrp
```

Target order is deterministic after trimming, de-duplication, inventory expansion, and exclusions.

## Inventory Selection

```powershell
dispatch run ps .\Fix.ps1 --inventory .\hosts.yml --target kiosks --transport psrp
```

## Preview With Plan

```powershell
dispatch run ps .\Fix.ps1 --target PC001 --transport psrp --plan --output json
```

Planning does not prompt for passwords, decrypt DPAPI files, read Windows Credential Manager targets, read Key Vault secrets, or touch endpoints.

## Output Modes

```powershell
dispatch run ps .\Fix.ps1 --target PC001 --output rich
dispatch run ps .\Fix.ps1 --target PC001 --output json --no-progress
dispatch run ps .\Fix.ps1 --target PC001 --output ndjson
```

Use `rich` for humans and structured modes for automation.

## Config Override

```powershell
dispatch run ps .\Fix.ps1 --target PC001 --config .\config.yml
```

If `--config` is omitted, Dispatch loads `C:\ProgramData\Dispatch\config.yml` when present.

## Expected Exit Codes

Default expected exit code is `0`.

Installer-friendly runs may accept reboot-required exit codes:

```powershell
dispatch run ps .\Install.ps1 --target PC001 --expected-exit-code 0 --expected-exit-code 3010
```

Unexpected exit codes fail the target with a stable failure category.

## Script-Created Files

Dispatch captures process output automatically. For retrievable files, scripts should write under the remote Dispatch run folder:

```text
C:\ProgramData\Dispatch\Runs\<RunId>\logs\
C:\ProgramData\Dispatch\Runs\<RunId>\artifacts\
```

Recommended:

- Put script logs under `logs\`.
- Put reports, summaries, generated files, and diagnostics under `artifacts\`.
- Avoid writing secrets to stdout, stderr, logs, or artifacts.

If the script only writes to stdout/stderr and creates no artifact folders, the run can still succeed. Artifact status is usually `not-found`.

## Safe Example Script

```powershell
param([string]$Message = "dispatch-ok")

Write-Output $Message

$runRoot = $env:DISPATCH_RUN_ROOT
if ($runRoot) {
  New-Item -ItemType Directory -Force -Path (Join-Path $runRoot "logs") | Out-Null
  "Completed at $(Get-Date -Format o)" | Set-Content -Path (Join-Path $runRoot "logs\script.log")
}
```
