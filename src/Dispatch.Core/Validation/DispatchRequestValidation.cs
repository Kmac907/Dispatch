using Dispatch.Core.Models;

namespace Dispatch.Core.Validation;

public sealed record DispatchValidationError(string Code, string Message);

public sealed record DispatchRequestValidationResult(IReadOnlyList<DispatchValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static DispatchRequestValidationResult Success { get; } = new([]);
}

public static class DispatchRequestValidator
{
    public static DispatchRequestValidationResult Validate(DispatchRequest request)
    {
        var errors = new List<DispatchValidationError>();

        if (request.Targets.Count == 0)
        {
            errors.Add(new("TargetsRequired", "At least one target is required."));
        }

        if (request.ExpectedExitCodes.Count == 0)
        {
            errors.Add(new("ExpectedExitCodesRequired", "At least one expected exit code is required."));
        }

        if (!IsSupportedPayload(request.Transport, request.Payload.PayloadType))
        {
            errors.Add(new(
                "UnsupportedTransportPayload",
                $"{request.Transport.ToDispatchString()} does not support {request.Payload.PayloadType.ToString().ToLowerInvariant()} payloads in this release."));
        }

        return errors.Count == 0
            ? DispatchRequestValidationResult.Success
            : new DispatchRequestValidationResult(errors);
    }

    public static bool IsSupportedPayload(TransportKind transport, PayloadKind payload) =>
        transport == TransportKind.PsExec && payload == PayloadKind.Script;
}
