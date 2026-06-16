using Dispatch.Core.Models;
using Dispatch.Core.Validation;

namespace Dispatch.Core.Targeting;

public sealed record TargetResolutionResult(
    IReadOnlyList<TargetSpec> Targets,
    IReadOnlyList<DispatchValidationError> Errors,
    TransportKind? InventoryTransport = null,
    IReadOnlyDictionary<string, TransportKind?>? InventoryTransportPolicies = null)
{
    public bool IsValid => Errors.Count == 0;
}
