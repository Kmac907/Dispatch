# Dispatch

Dispatch is a Windows-native automation runner for endpoint administrators. It runs PowerShell scripts, commands, and declared jobs across Windows hosts through explicit transports such as PSRP, raw WinRM, and PsExec.

Dispatch is intentionally narrow: it is not an endpoint agent, package manager, or full configuration-management platform. Scripts own their payloads; Dispatch owns targeting, transport execution, credentials, logs, results, and operator visibility.

Project site: https://kmac907.github.io/Dispatch/

## Current And Planned V1 Surface

- Current ad-hoc execution: `dispatch run ps`, `dispatch run cmd`, and `dispatch run exe` where supported by the selected transport.
- Current declared-job subset: `dispatch apply <job.yml>` with selected multi-task `ps`, `cmd`, and `exe` jobs, plus `--plan` / `--check` rendering for selected `copy` tasks.
- Current push subset: `dispatch push <source> --dest <remote-path>` for single-file and recursive directory transfer over raw WinRM or PSRP, including `--transport auto` selection through inventory/config/default transport policy, optional replacement through `--overwrite`, target-local pre-replacement backup through `--backup`, explicit push-result SHA-256 enforcement/reporting through `--checksum`, single-file `.ps1` execute-after-copy through `--execute`, post-execute script removal through `--cleanup`, plus `--plan` / `--check` preview.
- Current starter scaffolding: `dispatch init config`, `dispatch init hosts`, `dispatch init job`, and `dispatch init all`.
- Current host selection through direct targets, target files, inventories, groups, and selectors on implemented run paths.
- Explicit transports: `psrp`, `winrm`, and `psexec`.
- Live Spectre.Console run dashboard with honest phase/status reporting.
- Durable run history under `C:\ProgramData\Dispatch\Runs`.
- Canonical structured event log: `Admin\events.ndjson`.
- Final run summary: `Admin\results.json`.
- Per-target `stdout.txt`, `stderr.txt`, and collected script-created artifacts.
- Current credential references through prompt, DPAPI file, Windows Credential Manager, and Azure Key Vault providers on implemented PSRP and raw WinRM paths.
- Script secret handoff is separate from endpoint credentials: `--credential <name>` selects the remoting credential, while `dispatch run ps ... --secret name=reference` describes a script input secret. Current support validates the option shape and renders only the redacted script parameter binding in plan/dry-run output. Runtime provider resolution and safe transport parameter binding are planned Roadmap 10 work.
- Machine-wide YAML config at `C:\ProgramData\Dispatch\config.yml`.
- Current PowerShell module diagnostic/version wrappers plus `Invoke-DispatchPowerShell`, `Invoke-DispatchCommand`, `Invoke-DispatchExecutable`, and `Invoke-DispatchJob` over the same `dispatch.exe` command surface; `packaging/build-module.ps1` assembles a module folder with bundled `bin\win-x64\dispatch.exe`, and `packaging/install.ps1` installs an assembled package into a PowerShell module scope. Module wrappers support protected PSCredential handoff for configured `provider: pscredential` references.

## Choosing Commands

- Use `dispatch run` when the goal is ad-hoc execution with Dispatch-owned run history, results, stdout/stderr, and artifacts.
- Use `dispatch apply` when the work should be declared as a repeatable YAML job.
- Use `dispatch push` when the goal is copying a file or directory to a specific remote path; `--execute` is only a follow-up for pushed single `.ps1` files.
- Use `dispatch logs`, `dispatch creds`, `dispatch hosts`, `dispatch doctor`, and `dispatch init` for history, credential references, inventory inspection, diagnostics, and starter YAML.

## Get Started

### Run From Source

The current repository can be built and run directly from source:

```powershell
git clone https://github.com/Kmac907/Dispatch.git
cd Dispatch
dotnet build .\Dispatch.sln
dotnet test .\Dispatch.sln
dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- --help
```

The current source-install flow can build and install from a checkout:

```powershell
.\packaging\install-from-source.ps1 -Scope CurrentUser -Force
```

It is also designed for the GitHub `irm` flow, which clones the repository before building and installing:

```powershell
irm https://raw.githubusercontent.com/Kmac907/Dispatch/main/packaging/install-from-source.ps1 | iex
```

To only assemble and install an already built module package from a checkout:

```powershell
.\packaging\build-module.ps1
.\packaging\install.ps1 -Scope CurrentUser -Force
Import-Module Dispatch -Force
Get-DispatchVersion
```

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

Script secret handoff uses a different surface: `dispatch run ps ... --secret name=reference`. The script declares a matching parameter, such as `param([string]$packageSas)`, and Dispatch maps `name` to `-packageSas`. Current support validates the option shape, rejects duplicate or plaintext-looking values, and renders only `-packageSas [redacted]` in plan/dry-run output without resolving the provider reference. Roadmap 10 owns real execution: Dispatch must resolve `reference` from a configured secret provider on the admin side, bind the resolved value to the script parameter through the selected transport, and keep the value out of command lines, logs, result files, traces, artifacts, and structured output.

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

The planned v1 source installer will also support running from an existing checkout with `-NoCleanup` after roadmap item `8` is implemented.

## Documentation

- [Project Site](https://kmac907.github.io/Dispatch/)
- [Documentation Home](docs/readme.html)
- [Getting Started](docs/getting-started.html)
- [Command Reference](docs/command-reference.html)
- [Inventory Schema](docs/inventory-schema.html)
- [Transports](docs/transports.html)
- [Configuration](docs/configuration.html)
- [Output And Results](docs/output-and-results.html)
- [Troubleshooting](docs/troubleshooting.html)
- [Security](docs/security.html)
- [Roadmap Status](docs/roadmap-status.html)

## Security Notes

- Do not pass passwords, SAS tokens, or secrets on the command line.
- Store only credential references in job, host, and config YAML.
- Do not use `--credential` for script inputs; script secrets use `--secret name=reference` and redacted script-parameter handoff.
- Keep validation host names in ignored local files such as `workflow/build/test-hosts.yml`.
- Dispatch does not remediate WinRM, firewall, delegation, endpoint policy, or admin-share configuration.
