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
    private const string DpapiFileProviderName = "dpapi_file";
    private const string WindowsCredentialManagerProviderName = "windows_credential_manager";
    private const string AzureKeyVaultProviderName = "azure_keyvault";

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

        var validation = ValidateCredentialDefinition(normalizedName, credential);
        if (!validation.Succeeded)
        {
            return Task.FromResult(Failure(validation.Message, references));
        }

        var provider = validation.ProviderName;
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

        var validation = ValidateCredentialDefinition(normalizedName, credential);
        if (!validation.Succeeded)
        {
            return Task.FromResult(Failure(validation.Message, references));
        }

        var provider = validation.ProviderName;
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

    private CredentialDefinitionValidation ValidateCredentialDefinition(
        string name,
        IConfigurationSection credential)
    {
        var provider = ResolveProvider(credential);
        var missing = new List<string>();

        switch (provider.ToLowerInvariant())
        {
            case PromptProviderName:
            case PsCredentialProviderName:
                Require(credential, "Username", missing);
                break;

            case DpapiFileProviderName:
                Require(credential, "Username", missing);
                Require(credential, "Path", missing);
                break;

            case WindowsCredentialManagerProviderName:
                Require(credential, "Username", missing);
                Require(credential, "Target", missing);
                break;

            case AzureKeyVaultProviderName:
                Require(credential, "Username", missing);
                Require(credential, "VaultUri", missing);
                Require(credential, "SecretName", missing);
                Require(credential, "Auth", missing);

                var vaultUri = NormalizeOptionalValue(credential["VaultUri"]);
                if (!string.IsNullOrWhiteSpace(vaultUri)
                    && (!Uri.TryCreate(vaultUri, UriKind.Absolute, out var parsedUri)
                        || !parsedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                {
                    return CredentialDefinitionValidation.Failure(
                        provider,
                        $"Credential reference '{name}' has invalid azure_keyvault vault_uri. Use an absolute https:// URI.");
                }

                var auth = NormalizeOptionalValue(credential["Auth"]);
                if (!string.IsNullOrWhiteSpace(auth) && !IsSupportedAzureKeyVaultAuth(auth))
                {
                    return CredentialDefinitionValidation.Failure(
                        provider,
                        $"Credential reference '{name}' has unsupported azure_keyvault auth '{auth}'. Supported values: default_azure_credential, managed_identity, azure_cli.");
                }

                break;

            default:
                return CredentialDefinitionValidation.Failure(
                    provider,
                    $"Credential reference '{name}' uses unsupported provider '{provider}'. Supported providers: prompt, pscredential, dpapi_file, windows_credential_manager, azure_keyvault.");
        }

        return missing.Count == 0
            ? CredentialDefinitionValidation.Success(provider)
            : CredentialDefinitionValidation.Failure(
                provider,
                $"Credential reference '{name}' is missing required field(s) for provider '{provider}': {string.Join(", ", missing)}.");
    }

    private static void Require(IConfigurationSection credential, string key, ICollection<string> missing)
    {
        if (string.IsNullOrWhiteSpace(credential[key]))
        {
            missing.Add(ToYamlKey(key));
        }
    }

    private static bool IsSupportedAzureKeyVaultAuth(string auth) =>
        auth.Equals("default_azure_credential", StringComparison.OrdinalIgnoreCase)
        || auth.Equals("managed_identity", StringComparison.OrdinalIgnoreCase)
        || auth.Equals("azure_cli", StringComparison.OrdinalIgnoreCase);

    private static string ToYamlKey(string key) =>
        key switch
        {
            "Username" => "username",
            "Path" => "path",
            "Target" => "target",
            "VaultUri" => "vault_uri",
            "SecretName" => "secret_name",
            "Auth" => "auth",
            _ => key
        };

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

    private sealed record CredentialDefinitionValidation(
        bool Succeeded,
        string ProviderName,
        string Message)
    {
        public static CredentialDefinitionValidation Success(string providerName) =>
            new(Succeeded: true, providerName, string.Empty);

        public static CredentialDefinitionValidation Failure(string providerName, string message) =>
            new(Succeeded: false, providerName, message);
    }
}
