# Inventory

Inventories define which hosts Dispatch can target and optional metadata such as transport or credential references.

## Text Inventories

Simple host files are supported for direct host lists:

```text
PC001
PC002
# comments are ignored
PC003
```

Blank lines and comments are ignored. Duplicate host names are removed case-insensitively while preserving first-seen order.

## YAML Inventories

```yaml
defaults:
  transport: psrp
  credential: prod-admin

groups:
  kiosks:
    vars:
      transport: psrp
      credential: helpdesk-local
    hosts:
      - KIOSK01
      - KIOSK02

hosts:
  SERVER01:
    transport: winrm
    credential: domain-admin
  SERVER02:
```

## Selectors

Selectors name hosts or groups:

```powershell
dispatch run ps .\Fix.ps1 --inventory .\hosts.yml --target kiosks
dispatch run ps .\Fix.ps1 --inventory .\hosts.yml --target SERVER01
```

Comma-separated selectors are accepted where the command supports them.

## Excludes

Exclude selectors remove hosts after inclusion:

```powershell
dispatch run ps .\Fix.ps1 --inventory .\hosts.yml --target kiosks --exclude KIOSK02
```

## Ordering

Dispatch preserves deterministic ordering:

1. Read targets from CLI, file, or inventory in declared order.
2. Expand groups in declaration order.
3. De-duplicate case-insensitively while keeping first occurrence.
4. Apply excludes.

## Current Boundaries

Current inventory support covers the v1 host/group/defaults subset. Advanced Ansible-style expressions, facts, vars files, dynamic inventory plugins, roles, handlers, and module ecosystems are not v1 features.
