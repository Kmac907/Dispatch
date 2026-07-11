using Dispatch.Core.Models;
using Dispatch.Core.Execution;

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
        else if (request.ScriptSecrets.Count > 0)
        {
            errors.Add(new(
                "ScriptSecretsRequireScriptPayload",
                "Script secret handoff is supported only for PowerShell script payloads in this slice."));
        }

        AddScriptSecretReferenceErrors(request.ScriptSecrets, errors);

        AddArtifactPathErrors(request.ArtifactPaths, errors);
        AddPsrpExecutionContextErrors(request, errors);

        return errors.Count == 0
            ? DispatchRequestValidationResult.Success
            : new DispatchRequestValidationResult(errors);
    }

    public static bool IsSupportedPayload(TransportKind transport, PayloadKind payload) =>
        (transport, payload) switch
        {
            (TransportKind.PsExec, PayloadKind.Script) => true,
            (TransportKind.Psrp, PayloadKind.Script) => true,
            (TransportKind.Psrp, PayloadKind.Command) => true,
            (TransportKind.WinRm, PayloadKind.Script) => true,
            (TransportKind.WinRm, PayloadKind.Command) => true,
            _ => false
        };

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

    private static void AddScriptSecretReferenceErrors(
        IReadOnlyList<ScriptSecretReference> secrets,
        ICollection<DispatchValidationError> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var secret in secrets)
        {
            if (!IsValidSecretName(secret.Name))
            {
                errors.Add(new(
                    "InvalidScriptSecretName",
                    $"Script secret name '{secret.Name}' must start with a letter or underscore and contain only letters, numbers, or underscores."));
            }

            if (!names.Add(secret.Name))
            {
                errors.Add(new(
                    "DuplicateScriptSecretName",
                    $"Script secret name '{secret.Name}' is specified more than once."));
            }

            if (string.IsNullOrWhiteSpace(secret.ReferenceName))
            {
                errors.Add(new(
                    "InvalidScriptSecretReference",
                    $"Script secret '{secret.Name}' must reference a configured secret name."));
            }

            if (LooksLikeSecretValue(secret.ReferenceName))
            {
                errors.Add(new(
                    "PlaintextScriptSecretNotSupported",
                    $"Script secret '{secret.Name}' looks like a plaintext secret value. Use a configured secret reference name instead."));
            }
        }
    }

    private static bool IsValidSecretName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var first = value[0];
        return (char.IsLetter(first) || first == '_')
            && value.All(static character => char.IsLetterOrDigit(character) || character == '_');
    }

    private static void AddArtifactPathErrors(
        IReadOnlyList<string> artifactPaths,
        ICollection<DispatchValidationError> errors)
    {
        foreach (var artifactPath in artifactPaths)
        {
            if (string.IsNullOrWhiteSpace(artifactPath))
            {
                errors.Add(new("InvalidArtifactPath", "Artifact paths must not be empty."));
                continue;
            }

            var normalized = ArtifactCollectionPathResolver.NormalizeWindowsPath(artifactPath);
            var isDriveQualifiedAbsolute = ArtifactCollectionPathResolver.IsDriveQualifiedAbsoluteWindowsPath(normalized);
            if (normalized.StartsWith(@"\\", StringComparison.Ordinal)
                || normalized.StartsWith('\\')
                || normalized.Length >= 2 && normalized[1] == ':' && !isDriveQualifiedAbsolute)
            {
                errors.Add(new("InvalidArtifactPath", $"Artifact path '{artifactPath}' must be relative to the remote Dispatch run folder or a drive-qualified absolute remote path."));
                continue;
            }

            if (artifactPath.Contains('*') || artifactPath.Contains('?'))
            {
                errors.Add(new("InvalidArtifactPath", $"Artifact path '{artifactPath}' must be a simple relative folder path, not a glob."));
                continue;
            }

            var pathToInspect = isDriveQualifiedAbsolute ? normalized[3..] : normalized;
            if (isDriveQualifiedAbsolute && string.IsNullOrWhiteSpace(pathToInspect))
            {
                errors.Add(new("InvalidArtifactPath", $"Artifact path '{artifactPath}' must name a folder below the drive root."));
                continue;
            }

            if (pathToInspect.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                errors.Add(new("InvalidArtifactPath", $"Artifact path '{artifactPath}' contains invalid path characters."));
                continue;
            }

            var segments = pathToInspect.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
            {
                errors.Add(new("InvalidArtifactPath", $"Artifact path '{artifactPath}' must not contain current-directory or parent-directory segments."));
                continue;
            }

            if (segments.Any(static segment => segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                errors.Add(new("InvalidArtifactPath", $"Artifact path '{artifactPath}' contains invalid path characters."));
            }
        }
    }

    private static void AddPsrpExecutionContextErrors(
        DispatchRequest request,
        ICollection<DispatchValidationError> errors)
    {
        if (request.Transport != TransportKind.Psrp)
        {
            return;
        }

        if (request.ExecutionContext.PsrpConnectionKind != PsrpConnectionKind.WsMan)
        {
            errors.Add(new(
                "UnsupportedPsrpConnectionKind",
                "Dispatch PSRP currently supports WSMan only. PSRP-over-SSH remains a later roadmap slice."));
        }

        if (request.ExecutionContext.PsrpAuthentication is not (PsrpAuthenticationKind.Default or PsrpAuthenticationKind.Negotiate))
        {
            errors.Add(new(
                "UnsupportedPsrpAuthentication",
                "Dispatch PSRP currently supports current-user Default or Negotiate authentication only. Kerberos, CredSSP, Basic, and Certificate authentication remain later roadmap slices."));
        }

        if (!string.IsNullOrWhiteSpace(request.ExecutionContext.PsrpCertificateThumbprint)
            && request.ExecutionContext.PsrpAuthentication != PsrpAuthenticationKind.Certificate)
        {
            errors.Add(new(
                "InvalidPsrpCertificateThumbprint",
                "A PSRP certificate thumbprint may only be supplied when certificate authentication is selected."));
        }
    }
}
