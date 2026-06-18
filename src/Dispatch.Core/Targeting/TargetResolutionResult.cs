using Dispatch.Core.Models;
using Dispatch.Core.Validation;

namespace Dispatch.Core.Targeting;

public sealed record TargetResolutionResult(
    IReadOnlyList<TargetSpec> Targets,
    IReadOnlyList<DispatchValidationError> Errors,
    TransportKind? InventoryTransport = null,
    IReadOnlyDictionary<string, TransportKind?>? InventoryTransportPolicies = null,
    IReadOnlyDictionary<string, string?>? InventoryCredentialReferences = null)
{
    public bool IsValid => Errors.Count == 0;
}
