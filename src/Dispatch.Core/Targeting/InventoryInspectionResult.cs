using Dispatch.Core.Models;
using Dispatch.Core.Validation;

namespace Dispatch.Core.Targeting;

public sealed record InventoryInspectionResult(
    string InventoryPath,
    IReadOnlyList<InventoryHostInspection> Hosts,
    IReadOnlyList<DispatchValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record InventoryHostInspection(
    string Name,
    string? Source,
    IReadOnlyList<string> Groups,
    TransportKind? Transport,
    string? CredentialReference,
    bool? AllowPsExecFallback = null);

public sealed record InventoryGraphInspectionResult(
    string InventoryPath,
    IReadOnlyList<InventoryGraphGroup> Groups,
    IReadOnlyList<string> UngroupedHosts,
    IReadOnlyList<InventoryHostInspection> Hosts,
    IReadOnlyList<DispatchValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record InventoryGraphGroup(
    string Name,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<string> Children,
    TransportKind? Transport,
    string? CredentialReference,
    bool? AllowPsExecFallback = null);
