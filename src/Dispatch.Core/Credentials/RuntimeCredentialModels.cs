using System.Security;

namespace Dispatch.Core.Credentials;

public sealed class DispatchResolvedCredential : IDisposable
{
    public DispatchResolvedCredential(
        string referenceName,
        string userName,
        string providerName,
        SecureString password)
    {
        ReferenceName = string.IsNullOrWhiteSpace(referenceName)
            ? throw new ArgumentException("Credential reference name is required.", nameof(referenceName))
            : referenceName.Trim();
        UserName = string.IsNullOrWhiteSpace(userName)
            ? throw new ArgumentException("Credential username is required.", nameof(userName))
            : userName.Trim();
        ProviderName = string.IsNullOrWhiteSpace(providerName)
            ? throw new ArgumentException("Credential provider name is required.", nameof(providerName))
            : providerName.Trim();
        Password = password ?? throw new ArgumentNullException(nameof(password));
    }

    public string ReferenceName { get; }

    public string UserName { get; }

    public string ProviderName { get; }

    public SecureString Password { get; }

    public void Dispose() => Password.Dispose();
}

public sealed record RuntimeCredentialPromptRequest(
    string ReferenceName,
    string UserName,
    string ProviderName);

public interface IRuntimeCredentialPrompt
{
    Task<SecureString> PromptForPasswordAsync(
        RuntimeCredentialPromptRequest request,
        CancellationToken cancellationToken);
}

public sealed record RuntimeCredentialResolutionResult(
    bool Succeeded,
    string? FailureMessage,
    IReadOnlyDictionary<string, DispatchResolvedCredential> Credentials)
{
    public static RuntimeCredentialResolutionResult Success(
        IReadOnlyDictionary<string, DispatchResolvedCredential> credentials) =>
        new(true, null, credentials);

    public static RuntimeCredentialResolutionResult Failed(string message) =>
        new(false, message, new Dictionary<string, DispatchResolvedCredential>(StringComparer.OrdinalIgnoreCase));
}

public interface IRuntimeCredentialResolver
{
    Task<RuntimeCredentialResolutionResult> ResolveAsync(
        IEnumerable<string> credentialReferences,
        CancellationToken cancellationToken);
}

public sealed class UnavailableRuntimeCredentialResolver : IRuntimeCredentialResolver
{
    public Task<RuntimeCredentialResolutionResult> ResolveAsync(
        IEnumerable<string> credentialReferences,
        CancellationToken cancellationToken) =>
        Task.FromResult(RuntimeCredentialResolutionResult.Failed(
            "Runtime credential resolution is not available in this host."));
}
