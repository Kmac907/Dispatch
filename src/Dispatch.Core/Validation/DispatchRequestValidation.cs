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

        if (request.Payload is ScriptPayload scriptPayload)
        {
            AddScriptArgumentSecretErrors(scriptPayload, errors);
        }

        return errors.Count == 0
            ? DispatchRequestValidationResult.Success
            : new DispatchRequestValidationResult(errors);
    }

    public static bool IsSupportedPayload(TransportKind transport, PayloadKind payload) =>
        transport == TransportKind.PsExec && payload == PayloadKind.Script;

    private static void AddScriptArgumentSecretErrors(ScriptPayload payload, ICollection<DispatchValidationError> errors)
    {
        for (var index = 0; index < payload.ScriptArguments.Count; index++)
        {
            var argument = payload.ScriptArguments[index];
            if (LooksLikeSecretArgumentName(argument))
            {
                errors.Add(new(
                    "CommandLineSecretNotSupported",
                    $"Script argument '{argument}' looks like a credential, SAS, or secret parameter. Dispatch v1 does not support command-line secret handoff."));
                continue;
            }

            if (LooksLikeSecretValue(argument))
            {
                errors.Add(new(
                    "CommandLineSecretNotSupported",
                    $"Script argument at position {index + 1} looks like a credential, SAS, or secret value. Dispatch v1 does not support command-line secret handoff."));
            }
        }
    }

    private static bool LooksLikeSecretArgumentName(string value)
    {
        var normalized = value.TrimStart('-', '/').Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase);
        return normalized.Contains("password", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("sastoken", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("sas", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSecretValue(string value) =>
        value.Contains("sig=", StringComparison.OrdinalIgnoreCase)
        || value.Contains("SharedAccessSignature=", StringComparison.OrdinalIgnoreCase)
        || value.Contains("sv=", StringComparison.OrdinalIgnoreCase) && value.Contains("se=", StringComparison.OrdinalIgnoreCase) && value.Contains("sp=", StringComparison.OrdinalIgnoreCase);
}
