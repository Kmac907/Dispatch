# Testing and Validation

Dispatch validation has two layers:

- automated validation through unit and CLI tests
- live endpoint validation for slices that execute against Windows endpoints

Automated tests prove local contracts, planning, parsing, rendering, result modeling, and transport logic that can be exercised without a real endpoint. Live endpoint validation proves that an endpoint-execution slice works against at least one real Windows machine in the current environment.

## Automated Tests

Run the solution test suite before completing validation for a slice that changes shared behavior:

```powershell
dotnet test .\Dispatch.sln
```

For narrower changes, run the relevant project or filtered tests plus any broader regression tests needed for the touched behavior.

## Live Endpoint Validation

Live endpoint validation is required for endpoint-execution slices whenever the current environment and selected slice allow it. It should use the least invasive command practical, usually `whoami` or a temporary file under the Dispatch run folder.

Live validation only needs to pass on one user-approved reachable Windows endpoint. If multiple devices are provided and at least one reachable approved device passes the relevant live test, the live validation requirement is satisfied for that slice.

An offline, powered-down, disconnected, or unreachable device does not make the product validation fail by itself. It is an environment availability issue. Report the unavailable device separately and include the reason when it is known, such as failed name resolution, failed ping/connectivity, closed WinRM ports, timeout, or missing admin-share access.

If every supplied validation device is unavailable, live validation is blocked. The operator needs to connect and power on at least one device, restore network access, or provide replacement test devices before Dispatch can claim live endpoint validation for that slice.

## Reporting Live Results

When reporting live endpoint validation, include:

- the command that was run
- which target passed
- which targets were unavailable
- whether unavailable targets were excluded as environment availability issues
- any failure categories or connection details that help the operator fix the lab state

Do not report a live validation pass if no endpoint was actually reached and executed.

## Local Test Host Catalog

Concrete lab host names must not be committed in public docs or tracked files. Keep the current approved validation targets in `workflow/build/test-hosts.yml`, which is ignored by git through the existing `/workflow` ignore rule.

The local test-host file should list approved endpoints by transport and can be changed per workstation, lab, or validation environment. If one listed device is offline, validation can still pass against another reachable approved endpoint. If every listed device is offline or unreachable, provide a reachable replacement endpoint or reconnect one of the existing devices before claiming live endpoint validation for that slice.
