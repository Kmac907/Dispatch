namespace Dispatch.Core.Credentials;

public interface ICredentialProvider
{
    Task<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<CredentialProviderOperationResult> AddAsync(CredentialAddRequest request, CancellationToken cancellationToken);

    Task<CredentialProviderOperationResult> ListAsync(CancellationToken cancellationToken);

    Task<CredentialProviderOperationResult> TestAsync(CredentialReferenceRequest request, CancellationToken cancellationToken);

    Task<CredentialProviderOperationResult> RemoveAsync(CredentialReferenceRequest request, CancellationToken cancellationToken);
}
