using Dispatch.Core.Validation;

namespace Dispatch.Core.Execution;

public sealed class DispatchPlanningException : Exception
{
    public DispatchPlanningException(IReadOnlyList<DispatchValidationError> errors)
        : base(CreateMessage(errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<DispatchValidationError> Errors { get; }

    private static string CreateMessage(IReadOnlyList<DispatchValidationError> errors) =>
        errors.Count == 0
            ? "Dispatch request planning failed."
            : "Dispatch request planning failed: " + string.Join("; ", errors.Select(static error => error.Message));
}
