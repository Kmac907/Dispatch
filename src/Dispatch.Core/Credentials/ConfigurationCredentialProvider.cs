using Dispatch.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Dispatch.Core.Credentials;

public sealed class ConfigurationCredentialProvider(
    IConfiguration configuration,
    IOptions<DispatchOptions> options) : ICredentialProvider
{
    public const string ProviderName = "config";

    private const string CredentialsSectionName = "Credentials";
    private const string PromptProviderName = "prompt";
    private const string PsCredentialProviderName = "pscredential";

    public Task<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new CredentialProviderStatus(
            ProviderName,
            IsAvailable: true,
            "Credential references are loaded from Dispatch config. No plaintext secrets are stored."));

    public Task<CredentialProviderOperationResult> AddAsync(
        CredentialAddRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedName = NormalizeName(request.Name);
        var references = ListReferences();
        if (normalizedName is null)
        {
            return Task.FromResult(Failure("Credential name is required.", references));
        }

        if (!TryFindCredential(normalizedName, out var credential))
        {
            return Task.FromResult(Failure($"Credential reference '{normalizedName}' is not defined in Dispatch config.", references));
        }

        var provider = ResolveProvider(credential);
        return Task.FromResult(provider.Equals(PromptProviderName, StringComparison.OrdinalIgnoreCase)
            ? Success($"No enrollment required. Credential '{normalizedName}' will prompt at runtime.", references)
            : provider.Equals(PsCredentialProviderName, StringComparison.OrdinalIgnoreCase)
                ? Failure("PSCredential credentials are supplied by the PowerShell wrapper at runtime and cannot be enrolled by dispatch.exe.", references)
                : Failure($"Credential provider '{provider}' enrollment is not implemented in this build.", references));
    }

    public Task<CredentialProviderOperationResult> ListAsync(CancellationToken cancellationToken)
    {
        var references = ListReferences();
        return Task.FromResult(Success("Credential references listed from Dispatch config.", references));
    }

    public Task<CredentialProviderOperationResult> TestAsync(
        CredentialReferenceRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedName = NormalizeName(request.Name);
        var references = ListReferences();
        if (normalizedName is null)
        {
            return Task.FromResult(Failure("Credential name is required.", references));
        }

        if (!TryFindCredential(normalizedName, out var credential))
        {
            return Task.FromResult(Failure($"Credential reference '{normalizedName}' is not defined in Dispatch config.", references));
        }

        var provider = ResolveProvider(credential);
        return Task.FromResult(Success($"Credential reference '{normalizedName}' is defined with provider '{provider}'.", references));
    }

    public Task<CredentialProviderOperationResult> RemoveAsync(
        CredentialReferenceRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(Failure(
            "Config-defined credential references must be removed from Dispatch config.",
            ListReferences()));

    private IReadOnlyList<CredentialReference> ListReferences() =>
        configuration
            .GetSection(CredentialsSectionName)
            .GetChildren()
            .Select(static section => new CredentialReference(section.Key, NormalizeOptionalValue(section["Username"])))
            .OrderBy(static reference => reference.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private bool TryFindCredential(string name, out IConfigurationSection credential)
    {
        credential = configuration.GetSection($"{CredentialsSectionName}:{name}");
        return credential.Exists();
    }

    private string ResolveProvider(IConfigurationSection credential)
    {
        var provider = NormalizeOptionalValue(credential["Provider"]);
        if (!string.IsNullOrWhiteSpace(provider))
        {
            return provider;
        }

        var configuredDefault = NormalizeOptionalValue(options.Value.CredentialProvider);
        return string.IsNullOrWhiteSpace(configuredDefault)
               || configuredDefault.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? PromptProviderName
            : configuredDefault;
    }

    private static string? NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static string? NormalizeOptionalValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CredentialProviderOperationResult Success(
        string message,
        IReadOnlyList<CredentialReference> references) =>
        new(ProviderName, ProviderAvailable: true, Succeeded: true, message, references);

    private static CredentialProviderOperationResult Failure(
        string message,
        IReadOnlyList<CredentialReference> references) =>
        new(ProviderName, ProviderAvailable: true, Succeeded: false, message, references);
}
