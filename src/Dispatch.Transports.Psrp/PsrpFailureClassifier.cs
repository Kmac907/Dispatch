using Dispatch.Core.Models;

namespace Dispatch.Transports.Psrp;

internal static class PsrpFailureClassifier
{
    public static FailureCategory Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return FailureCategory.TransportUnavailable;
        }

        if (message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
        {
            return FailureCategory.AuthorizationFailed;
        }

        if (message.Contains("Logon failure", StringComparison.OrdinalIgnoreCase)
            || message.Contains("The user name or password is incorrect", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Kerberos", StringComparison.OrdinalIgnoreCase))
        {
            return FailureCategory.AuthenticationFailed;
        }

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("OperationTimeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("OpenTimeout", StringComparison.OrdinalIgnoreCase))
        {
            return FailureCategory.TimedOut;
        }

        if (message.Contains("Connecting to remote server", StringComparison.OrdinalIgnoreCase)
            || message.Contains("WinRM client cannot process the request", StringComparison.OrdinalIgnoreCase)
            || message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase))
        {
            return FailureCategory.TransportUnavailable;
        }

        return FailureCategory.ExecutionFailed;
    }
}
