# Dispatch

Dispatch is a Windows-native automation runner for endpoint administrators. It runs PowerShell scripts, commands, and declared jobs across Windows hosts through explicit transports such as PSRP, raw WinRM, and PsExec.

Dispatch is intentionally narrow: it is not an endpoint agent, package manager, or full configuration-management platform. Scripts own their payloads; Dispatch owns targeting, transport execution, credentials, logs, results, and operator visibility.

Project site: https://kmac907.github.io/Dispatch/

## Features

- Ad-hoc execution: `dispatch run ps`, `dispatch run cmd`, and `dispatch run exe`.
- Declared jobs: `dispatch apply <job.yml>`.
- Host selection through direct targets, inventories, groups, and selectors.
- Explicit transports: `psrp`, `winrm`, and `psexec`.
- Live Spectre.Console run dashboard with honest phase/status reporting.
- Durable run history under `C:\ProgramData\Dispatch\Runs`.
- Canonical structured event log: `Admin\events.ndjson`.
- Final run summary: `Admin\results.json`.
- Per-target `stdout.txt`, `stderr.txt`, and collected script-created artifacts.
- Credential references through prompt, DPAPI file, Windows Credential Manager, and Azure Key Vault providers.
- Machine-wide YAML config at `C:\ProgramData\Dispatch\config.yml`.
- Optional PowerShell module wrapper over the same `dispatch.exe` command surface.

## Get Started

### Install From Source

The v1 source-install path is a GitHub-hosted PowerShell bootstrap. Run it from an elevated PowerShell session when installing for all users.

```powershell
irm https://raw.githubusercontent.com/Kmac907/Dispatch/main/packaging/install-from-source.ps1 | iex
```

The installer builds Dispatch from source, publishes a single-file `dispatch.exe`, installs the PowerShell module wrapper, validates the install, and cleans up the temporary source checkout. Use the local developer flow below when you want to keep the repository checkout.

### Verify

```powershell
dispatch --help
dispatch doctor
dispatch version
```

### Run A Script

```powershell
dispatch run ps .\Fix.ps1 --target PC001,PC002 --transport psrp
```

Preview the run without touching endpoints:

```powershell
dispatch run ps .\Fix.ps1 --target PC001,PC002 --transport psrp --plan --output json
```

Use a YAML inventory:

```powershell
dispatch run ps .\Fix.ps1 --inventory .\hosts.yml --target kiosks --transport psrp
```

## Configuration

Dispatch loads the global machine config by default:

```text
C:\ProgramData\Dispatch\config.yml
```

Example:

```yaml
dispatch:
  default_transport: psrp
  default_credential_provider: prompt

credentials:
  prod-admin:
    provider: prompt
    username: CONTOSO\prod.admin
```

Credential references may appear in `job.yml`, `hosts.yml`, or `--credential <name>`. Provider details, usernames, store paths, Key Vault URIs, and secret names live in `config.yml`. Passwords and secret values do not belong in YAML.

See [Credentials](docs/credentials.md) for the operator-facing credential model.

## Outputs

Each run writes a local run folder:

```text
C:\ProgramData\Dispatch\Runs\<run-id>\
  Admin\
    events.ndjson
    results.json
  Targets\
    <target>\
      stdout.txt
      stderr.txt
      logs\
      artifacts\
```

Use `dispatch logs` to inspect previous runs:

```powershell
dispatch logs list
dispatch logs show latest
dispatch logs tail latest --count 50
dispatch logs export latest --dest .\exports
```

See [Output And Results](docs/output-and-results.md).

## Project Structure

```text
Dispatch/
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
    Dispatch/
  packaging/
  workflow/
```

`src/` contains the product code. `tests/` contains automated tests. `docs/` contains public documentation. `module/` and `packaging/` are the v1 install/package surfaces. `workflow/` is ignored local execution state for roadmap tracking and validation hosts.

## Development

```powershell
git clone https://github.com/Kmac907/Dispatch.git
cd Dispatch
dotnet build .\Dispatch.sln
dotnet test .\Dispatch.sln
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- --help
```

Run from source:

```powershell
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- run ps .\Fix.ps1 --target PC001 --transport psrp --plan
```

Install from an existing checkout:

```powershell
.\packaging\install-from-source.ps1 -NoCleanup
```

## Documentation

- [Project Site](https://kmac907.github.io/Dispatch/)
- [Getting Started](docs/getting-started.html)
- [Installation](docs/installation.md)
- [CLI Reference](docs/cli.html)
- [Configuration](docs/configuration.html)
- [Inventories And Jobs](docs/inventory-and-jobs.html)
- [Credentials](docs/credentials.md)
- [PowerShell Module](docs/powershell-module.html)
- [Output And Results](docs/output-and-results.md)
- [Script-Owned Payloads](docs/script-owned-payloads.md)
- [Testing And Validation](docs/testing-and-validation.md)

## Security Notes

- Do not pass passwords, SAS tokens, or secrets on the command line.
- Store only credential references in job, host, and config YAML.
- Keep validation host names in ignored local files such as `workflow/build/test-hosts.yml`.
- Dispatch does not remediate WinRM, firewall, delegation, endpoint policy, or admin-share configuration.
