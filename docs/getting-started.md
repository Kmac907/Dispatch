# Getting Started

This guide is the shortest path from a clean checkout or install to a verified Dispatch run.

## Prerequisites

- Windows 10/11 or Windows Server.
- PowerShell 7 or Windows PowerShell 5.1.
- .NET SDK matching `global.json` when building from source.
- Git on `PATH` for source install.
- At least one approved Windows endpoint for live validation when running remote commands.
- PSRP or WinRM enabled on the endpoint when using those transports, or SMB/admin-share access when using PsExec.

## Install

The v1 source-install path downloads the installer from GitHub and builds Dispatch locally:

```powershell
irm https://raw.githubusercontent.com/Kmac907/Dispatch/main/packaging/install-from-source.ps1 | iex
```

Use an elevated shell for all-users installation.

## Run From Source

```powershell
git clone https://github.com/Kmac907/Dispatch.git
cd Dispatch
dotnet build .\Dispatch.sln
dotnet test .\Dispatch.sln
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- --help
```

## Validate Locally

```powershell
dispatch --help
dispatch version
dispatch doctor
```

`dispatch doctor` checks local prerequisites and reports problems. It does not repair endpoint remoting, firewall, delegation, admin-share, or policy settings.

## Create A Small Inventory

Use a text file for the simplest target list:

```text
PC001
PC002
```

Use YAML when you need defaults, groups, credentials, or transport metadata:

```yaml
defaults:
  transport: psrp
groups:
  kiosks:
    hosts: [KIOSK01, KIOSK02]
hosts:
  SERVER01:
    transport: winrm
```

Inventory files only select host metadata and credential references. Credential provider details live in the global Dispatch config.

## Preview A Run

```powershell
dispatch run ps .\Fix.ps1 --target PC001 --transport psrp --plan --output json
```

Planning validates local inputs and produces the execution plan before endpoint work starts.

## Run A Script

```powershell
dispatch run ps .\Fix.ps1 --target PC001,PC002 --transport psrp
```

Use `--transport winrm` for raw WS-Management shell execution or `--transport psexec` where admin-share staging and PsExec policy are available.

Run against an inventory group:

```powershell
dispatch run ps .\Fix.ps1 --inventory .\hosts.yml --target kiosks --transport psrp
```

Pass ordinary script arguments after the script path:

```powershell
dispatch run ps .\Fix.ps1 -Mode Repair -Verbose --target PC001 --transport psrp
```

## Find Results

Runs are written under:

```text
C:\ProgramData\Dispatch\Runs\<run-id>\
```

Important files:

- `Admin\events.ndjson` - canonical structured run event stream.
- `Admin\results.json` - final reduced run summary.
- `Targets\<target>\stdout.txt` - captured target stdout.
- `Targets\<target>\stderr.txt` - captured target stderr.
- `Targets\<target>\artifacts\...` - copied-back script-created files when present.

Inspect previous runs:

```powershell
dispatch logs list
dispatch logs show latest
dispatch logs tail latest --count 50
```

## If The First Remote Run Fails

- Confirm the target resolves from the admin workstation.
- Confirm the selected transport is enabled and reachable.
- Use PSRP or raw WinRM when SMB/admin-share staging is not available.
- Use `--plan --output json` to verify target, credential, and transport selection before retrying endpoint work.
- If one approved live endpoint passes and another is offline, treat the offline endpoint as an environment availability issue, not a product validation failure.
