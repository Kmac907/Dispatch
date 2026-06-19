# Dispatch Credentials

Dispatch uses credential references so jobs and inventories can name a credential without embedding plaintext secrets.

The canonical credential store and resolution design is [Credential Store Plan](credential-store-plan.md). That file defines the global `C:\ProgramData\Dispatch\config.yml` credential catalog, provider settings, enrollment behavior, Azure Key Vault auth modes, and precedence rules.

Current implementation status:

- `dispatch creds add|list|test|remove` is wired to a credential provider abstraction.
- The default provider is `none` and reports unavailable.
- Configuring `Dispatch:CredentialProvider` as `file` or `local` enables a file-backed reference catalog at `Dispatch:CredentialStorePath`, defaulting to `C:\ProgramData\Dispatch\Credentials\references.json`.
- No plaintext password command-line flags are supported.
- No credential secret is stored by the default provider or by the file-backed provider. The file-backed provider stores reference names and optional username metadata only.
- YAML inventories in the current supported subset accept `credential: <name>` reference names on defaults, group vars, host vars, and hosts.
- YAML inventory validation rejects plaintext secret-like fields such as `password`, `secret`, `token`, `sas`, `sasToken`, and fields ending in `Password`, `Secret`, or `Token`.
- YAML job credential validation, config-file secret policy validation, runtime secret resolution, and transport credential handoff are later slices.

Examples:

```powershell
dispatch creds list
dispatch creds add prod-admin --username CONTOSO\Admin
dispatch creds test prod-admin
dispatch creds remove prod-admin
```

For automation, use structured output:

```powershell
dispatch creds list --output json
```

When no provider is configured, the command exits nonzero and reports provider availability. Structured output still includes the provider name, availability state, success state, message, and any references returned by the provider.

To enable local reference metadata storage:

```json
{
  "Dispatch": {
    "CredentialProvider": "file",
    "CredentialStorePath": "C:\\ProgramData\\Dispatch\\Credentials\\references.json"
  }
}
```

The file-backed provider writes a JSON catalog containing reference names and optional usernames. It does not prompt for, store, encrypt, decrypt, or hand off passwords.

Inventory reference behavior:

```yaml
defaults:
  credential: prod-default
groups:
  web:
    vars:
      credential: web-admin
    hosts: [WEB01]
hosts:
  WEB01:
    credential: host-admin
```

Reference names are metadata only in the current slice. They are validated and carried through target resolution, but no password is retrieved or handed to transports yet.

Security boundary:

- Do not pass passwords, SAS tokens, or other secrets on the command line.
- Credential reference names may appear in output and logs.
- Secret values must not appear in console output, JSON, NDJSON, YAML, durable logs, or dry-run output.
