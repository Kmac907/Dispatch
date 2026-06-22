namespace Dispatch.Core.Credentials;

public sealed record CredentialProviderStatus(
    string ProviderName,
    bool IsAvailable,
    string Message);

public sealed record CredentialReference(
    string Name,
    string? UserName);

public sealed record CredentialAddRequest(
    string Name,
    string? UserName,
    bool Force = false);

public sealed record CredentialReferenceRequest(
    string Name);

public sealed record CredentialProviderOperationResult(
    string ProviderName,
    bool ProviderAvailable,
    bool Succeeded,
    string Message,
    IReadOnlyList<CredentialReference> References);
