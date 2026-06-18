using Dispatch.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Dispatch.Core.Credentials;

public sealed class UnavailableCredentialProvider(IOptions<DispatchOptions> options) : ICredentialProvider
{
    private const string DefaultProviderName = "none";

    private string ProviderName => string.IsNullOrWhiteSpace(options.Value.CredentialProvider)
        ? DefaultProviderName
        : options.Value.CredentialProvider.Trim();

    public Task<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new CredentialProviderStatus(
            ProviderName,
            IsAvailable: false,
            CreateUnavailableMessage()));

    public Task<CredentialProviderOperationResult> AddAsync(
        CredentialAddRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(CreateUnavailableResult());

    public Task<CredentialProviderOperationResult> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult(CreateUnavailableResult());

    public Task<CredentialProviderOperationResult> TestAsync(
        CredentialReferenceRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(CreateUnavailableResult());

    public Task<CredentialProviderOperationResult> RemoveAsync(
        CredentialReferenceRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(CreateUnavailableResult());

    private CredentialProviderOperationResult CreateUnavailableResult() =>
        new(
            ProviderName,
            ProviderAvailable: false,
            Succeeded: false,
            CreateUnavailableMessage(),
            []);

    private string CreateUnavailableMessage() =>
        ProviderName.Equals(DefaultProviderName, StringComparison.OrdinalIgnoreCase)
            ? "No credential provider is configured. Dispatch can track credential reference commands, but this build does not store plaintext credentials."
            : $"Credential provider '{ProviderName}' is not available in this build. Dispatch will not store plaintext credentials.";
}
