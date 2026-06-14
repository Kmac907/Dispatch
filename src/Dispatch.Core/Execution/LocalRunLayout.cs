using Dispatch.Core.Models;
using Dispatch.Core.Validation;

namespace Dispatch.Core.Execution;

public sealed record TargetLocalLayout(
    TargetSpec Target,
    string LocalTargetRoot,
    string LocalResultPath);

public sealed record LocalRunLayout(
    string LocalRunRoot,
    string LocalAdminRoot,
    string LocalTargetsRoot,
    string LocalResultsJsonPath,
    string LocalResultsCsvPath,
    IReadOnlyList<TargetLocalLayout> Targets);

public sealed record LocalRunLayoutResult(
    LocalRunLayout? Layout,
    IReadOnlyList<DispatchValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0 && Layout is not null;
}
