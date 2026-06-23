# Inventory Schema

This is the v1 YAML inventory contract.

## Supported Top-Level Sections

```yaml
defaults:
groups:
hosts:
```

Unsupported top-level sections fail validation.

## Defaults

```yaml
defaults:
  transport: psrp
  credential: prod-admin
```

Supported fields:

- `transport`
- `credential`

## Groups

```yaml
groups:
  web:
    vars:
      transport: psrp
      credential: web-admin
    hosts:
      - WEB01
      - WEB02
    children:
      - web_canary
```

Supported group fields:

- `vars`
- `hosts`
- `children`

Supported `vars` fields:

- `transport`
- `credential`

## Hosts

Mapping form:

```yaml
hosts:
  SERVER01:
    transport: winrm
    credential: domain-admin
  SERVER02:
```

List form:

```yaml
hosts:
  - SERVER01
  - SERVER02
```

Inline list form:

```yaml
hosts: [SERVER01, SERVER02]
```

## Precedence

Transport selection:

1. Explicit CLI `--transport` when not `auto`.
2. Job transport when running `apply`, when not `auto`.
3. Host inventory transport.
4. Inherited group transport.
5. Inventory defaults transport.
6. Global config default transport.
7. Built-in default.

Credential reference selection:

1. CLI `--credential`.
2. Job credential.
3. Host credential.
4. Inherited group credential.
5. Inventory defaults credential.
6. No credential.

## Valid Example

```yaml
defaults:
  transport: psrp
  credential: prod-admin
groups:
  kiosks:
    vars:
      credential: helpdesk-local
    hosts: [KIOSK01, KIOSK02]
hosts:
  SERVER01:
    transport: winrm
```

## Invalid Examples

Plaintext secrets are invalid:

```yaml
hosts:
  PC001:
    password: bad
```

Unsupported fields are invalid:

```yaml
groups:
  web:
    vars_files:
      - web.yml
```

## Validation Behavior

Inventory validation fails before endpoint work starts. Errors should name the unsupported section, unsupported field, invalid type, unresolved selector, conflicting inherited metadata, or rejected secret-like key.
