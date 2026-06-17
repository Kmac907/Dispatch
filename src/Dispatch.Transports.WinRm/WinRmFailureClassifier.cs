using Dispatch.Core.Models;

namespace Dispatch.Transports.WinRm;

internal static class WinRmFailureClassifier
{
    public static FailureCategory Classify(string? message, IDictionary<string, string>? metadata)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return FailureCategory.ExecutionFailed;
        }

        if (ContainsAny(message,
                "access is denied",
                "requested access is denied",
                "authorizationmanager check failed",
                "not authorized"))
        {
            if (metadata is not null)
            {
                metadata["failureKind"] = "authorization";
            }
            return FailureCategory.AuthorizationFailed;
        }

        if (ContainsAny(message,
                "logon failure",
                "user name or password is incorrect",
                "unknown user name or bad password",
                "the specified credentials were rejected",
                "credentials cannot be used",
                "authentication failed"))
        {
            if (metadata is not null)
            {
                metadata["failureKind"] = "authentication";
            }
            return FailureCategory.AuthenticationFailed;
        }

        if (ContainsAny(message,
                "cannot connect",
                "connection failed",
                "actively refused",
                "no connection could be made",
                "network path was not found",
                "name cannot be resolved",
                "no such host is known",
                "timed out waiting",
                "ws-management service cannot process the request"))
        {
            if (metadata is not null)
            {
                metadata["failureKind"] = "transport";
            }
            return FailureCategory.TransportUnavailable;
        }

        return FailureCategory.ExecutionFailed;
    }

    public static FailureCategory Choose(IReadOnlyList<FailureCategory> categories)
    {
        if (categories.Contains(FailureCategory.AuthorizationFailed))
        {
            return FailureCategory.AuthorizationFailed;
        }

        if (categories.Contains(FailureCategory.AuthenticationFailed))
        {
            return FailureCategory.AuthenticationFailed;
        }

        if (categories.Contains(FailureCategory.TransportUnavailable))
        {
            return FailureCategory.TransportUnavailable;
        }

        return FailureCategory.ExecutionFailed;
    }

    private static bool ContainsAny(string message, params string[] patterns) =>
        patterns.Any(pattern => message.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
}
