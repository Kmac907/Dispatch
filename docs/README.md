# Dispatch Documentation

Dispatch is a Windows-native endpoint automation runner for administrators who need to run scripts, commands, and declared YAML jobs across Windows hosts with durable results and explicit transport behavior.

Dispatch is not an endpoint agent, package manager, or full configuration-management platform. It focuses on target selection, execution planning, transport execution, credential references, live status, logs, results, and copied-back script-created files.

Endpoint credentials and script secret handoff are separate models. `--credential <name>` selects the remoting credential. The script input surface is `dispatch run ps ... --secret name=reference`; current support validates the option shape and renders only a redacted binding such as `-packageSas [redacted]` in plan/dry-run output. Runtime provider resolution and safe transport parameter binding are planned Roadmap 10 work.

## Start Here

- [Getting Started](getting-started.html) - first install, validation, and run.
- [Installation](installation.html) - primary install, direct `dispatch` invocation, module import, source execution, and local package install behavior.
- [Concepts](concepts.html) - jobs, targets, inventories, transports, plans, runs, results, logs, and artifacts.
- [Command Reference](command-reference.html) - command tree, options, examples, and implementation status.
- [Running Scripts](running-scripts.html) - day-to-day `dispatch run ps` workflows.

## Operator Guides

- [Inventory](inventory.html)
- [Inventory Schema](inventory-schema.html)
- [Transports](transports.html)
- [PsExec Transport](transport-psexec.html)
- [Raw WinRM Transport](transport-winrm.html)
- [PSRP Transport](transport-psrp.html)
- [Configuration](configuration.html)
- [Output And Results](output-and-results.html)
- [Logs](logs.html)
- [Troubleshooting](troubleshooting.html)
- [Security](security.html)
- [Script-Owned Payloads](script-owned-payloads.html)

## Contracts And References

- [Credentials](credentials.html)
- [Error Codes](error-codes.html)
- [Result Schema](result-schema.html)
- [Examples](examples.html)
- [FAQ](faq.html)
- [Jobs](jobs.html)
- [Job Schema](job-schema.html)

## Maintainer Guides

- [Architecture](architecture.html)
- [Development](development.html)
- [Testing And Validation](testing-and-validation.html)
- [Roadmap Status](roadmap-status.html)
- [PowerShell Module](powershell-module.html)
- [Distribution](distribution.html)

## Deferred Feature Docs

- [Managed Execution](managed-execution.html)
- [Enterprise Distribution](enterprise-distribution.html)

Internal roadmap and documentation inventory files remain in the repository for maintainers, but they are not first-class public site pages.
