# Dispatch Credentials

Dispatch uses credential references so jobs and inventories can name a credential without embedding plaintext secrets.

Current implementation status:

- `dispatch creds add|list|test|remove` is wired to a credential provider abstraction.
- The default provider is `none` and reports unavailable.
- No plaintext password command-line flags are supported.
- No credential secret is stored by the default provider.
- YAML inventories in the current supported subset accept `credential: <name>` reference names on defaults, group vars, host vars, and hosts.
- YAML inventory validation rejects plaintext secret-like fields such as `password`, `secret`, `token`, `sas`, `sasToken`, and fields ending in `Password`, `Secret`, or `Token`.
- YAML job credential validation and real provider-backed storage/resolution are later slices.

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
