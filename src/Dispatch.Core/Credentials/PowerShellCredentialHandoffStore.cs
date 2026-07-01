using System.Security;
using System.Text.Json;

namespace Dispatch.Core.Credentials;

internal static class PowerShellCredentialHandoffStore
{
    public const string EnvironmentVariableName = "DISPATCH_PSCREDENTIAL_HANDOFF";

    private const int CurrentVersion = 1;
    private const string ProviderName = "pscredential";
    private const string ProtectionName = "dpapi_current_user";

    public static PowerShellCredentialHandoffReadResult ReadFromEnvironment(
        string referenceName,
        string configuredUserName)
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return PowerShellCredentialHandoffReadResult.NotPresent();
        }

        return Read(referenceName, configuredUserName, path);
    }

    public static PowerShellCredentialHandoffReadResult Read(
        string referenceName,
        string configuredUserName,
        string path)
    {
        var fullPath = Path.GetFullPath(path);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return PowerShellCredentialHandoffReadResult.Failed(
                    "PowerShell PSCredential handoff is supported on Windows only.");
            }

            if (!File.Exists(fullPath))
            {
                return PowerShellCredentialHandoffReadResult.Failed(
                    $"PowerShell PSCredential handoff file '{fullPath}' was not found.");
            }

            var file = JsonSerializer.Deserialize<PowerShellCredentialHandoffFile>(
                    File.ReadAllText(fullPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"PowerShell PSCredential handoff file '{fullPath}' is empty or invalid.");
            if (file.Version != CurrentVersion
                || !file.Provider.Equals(ProviderName, StringComparison.OrdinalIgnoreCase)
                || !file.Protection.Equals(ProtectionName, StringComparison.OrdinalIgnoreCase))
            {
                return PowerShellCredentialHandoffReadResult.Failed(
                    $"PowerShell PSCredential handoff file '{fullPath}' is not a supported Dispatch handoff file.");
            }

            if (!file.ReferenceName.Equals(referenceName, StringComparison.OrdinalIgnoreCase))
            {
                return PowerShellCredentialHandoffReadResult.Failed(
                    $"PowerShell PSCredential handoff file '{fullPath}' is for credential '{file.ReferenceName}', not '{referenceName}'.");
            }

            if (!file.UserName.Equals(configuredUserName, StringComparison.OrdinalIgnoreCase))
            {
                return PowerShellCredentialHandoffReadResult.Failed(
                    $"PowerShell PSCredential handoff username '{file.UserName}' does not match Dispatch config username '{configuredUserName}'.");
            }

            var protectedBytes = Convert.FromBase64String(file.ProtectedValue);
            var plaintext = DpapiCredentialFileStore.Unprotect(protectedBytes, $"Dispatch:pscredential:{referenceName}");
            try
            {
                var password = DpapiCredentialFileStore.Utf8BytesToSecureString(plaintext);
                password.MakeReadOnly();
                return PowerShellCredentialHandoffReadResult.Success(
                    new DispatchResolvedCredential(referenceName, file.UserName, ProviderName, password));
            }
            finally
            {
                DpapiCredentialFileStore.CryptographicOperationsZeroMemory(plaintext);
                DpapiCredentialFileStore.CryptographicOperationsZeroMemory(protectedBytes);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or InvalidOperationException or PlatformNotSupportedException)
        {
            return PowerShellCredentialHandoffReadResult.Failed(exception.Message);
        }
        finally
        {
            TryDelete(fullPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PowerShellCredentialHandoffFile(
        int Version,
        string Provider,
        string ReferenceName,
        string UserName,
        string Protection,
        string ProtectedValue,
        DateTimeOffset CreatedAt);
}

internal sealed record PowerShellCredentialHandoffReadResult(
    bool IsPresent,
    bool Succeeded,
    string? FailureMessage,
    DispatchResolvedCredential? Credential)
{
    public static PowerShellCredentialHandoffReadResult NotPresent() =>
        new(IsPresent: false, Succeeded: false, FailureMessage: null, Credential: null);

    public static PowerShellCredentialHandoffReadResult Success(DispatchResolvedCredential credential) =>
        new(IsPresent: true, Succeeded: true, FailureMessage: null, Credential: credential);

    public static PowerShellCredentialHandoffReadResult Failed(string message) =>
        new(IsPresent: true, Succeeded: false, FailureMessage: message, Credential: null);
}
