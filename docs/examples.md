# Examples

## Simple Script Run

```powershell
dispatch run ps .\Fix.ps1 --target PC001 --transport psrp
```

## Preview A Script Run

```powershell
dispatch run ps .\Fix.ps1 --target PC001 --transport psrp --plan --output json
```

## Multi-Target Run

```powershell
dispatch run cmd whoami --target PC001,PC002 --transport winrm --output json --no-progress
```

## Inventory

```yaml
defaults:
  transport: psrp
  credential: prod-admin
groups:
  kiosks:
    hosts: [KIOSK01, KIOSK02]
hosts:
  SERVER01:
```

```powershell
dispatch run ps .\Fix.ps1 --inventory .\hosts.yml --target kiosks
```

## Config

```yaml
dispatch:
  default_transport: psrp
  default_credential_provider: prompt
credentials:
  prod-admin:
    provider: prompt
    username: CONTOSO\prod.admin
```

## Credential Override

```powershell
dispatch run ps .\Fix.ps1 --target PC001 --credential prod-admin
```

## Logs

```powershell
dispatch logs list
dispatch logs show latest
dispatch logs export latest --dest .\exports
```

## Transport Examples

```powershell
dispatch run ps .\Fix.ps1 --target PC001 --transport psrp
dispatch run cmd whoami --target PC001 --transport winrm
dispatch run ps .\Fix.ps1 --target PC001 --transport psexec
```
