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
