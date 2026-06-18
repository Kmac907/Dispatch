# Dispatch Credentials

Dispatch uses credential references so jobs and inventories can name a credential without embedding plaintext secrets.

Current implementation status:

- `dispatch creds add|list|test|remove` is wired to a credential provider abstraction.
- The default provider is `none` and reports unavailable.
- No plaintext password command-line flags are supported.
- No credential secret is stored by the default provider.
- YAML `credential: <name>` resolution and real provider storage are later `6.4` slices.

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

Security boundary:

- Do not pass passwords, SAS tokens, or other secrets on the command line.
- Credential reference names may appear in output and logs.
- Secret values must not appear in console output, JSON, NDJSON, YAML, durable logs, or dry-run output.
