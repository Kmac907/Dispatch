using Dispatch.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Security;

namespace Dispatch.Core.Credentials;

public sealed class ConfigurationCredentialProvider(
    IConfiguration configuration,
    IOptions<DispatchOptions> options,
    IRuntimeCredentialPrompt? prompt = null,
    IAzureKeyVaultCredentialSecretResolver? azureKeyVaultSecrets = null) : ICredentialProvider
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

    public async Task<CredentialProviderOperationResult> AddAsync(
        CredentialAddRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedName = NormalizeName(request.Name);
        var references = ListReferences();
        if (normalizedName is null)
        {
            return Failure("Credential name is required.", references);
        }

        if (!TryFindCredential(normalizedName, out var credential))
        {
            return Failure($"Credential reference '{normalizedName}' is not defined in Dispatch config.", references);
        }

        var validation = ValidateCredentialDefinition(normalizedName, credential);
        if (!validation.Succeeded)
        {
            return Failure(validation.Message, references);
        }

        var provider = validation.ProviderName;
        if (provider.Equals(PromptProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return Success($"No enrollment required. Credential '{normalizedName}' will prompt at runtime.", references);
        }

        if (provider.Equals(PsCredentialProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("PSCredential credentials are supplied by the PowerShell wrapper at runtime and cannot be enrolled by dispatch.exe.", references);
        }

        if (provider.Equals(DpapiFileProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return await EnrollDpapiFileAsync(normalizedName, credential, request.Force, references, cancellationToken)
                .ConfigureAwait(false);
        }

        if (provider.Equals(WindowsCredentialManagerProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return await EnrollWindowsCredentialManagerAsync(normalizedName, credential, request.Force, references, cancellationToken)
                .ConfigureAwait(false);
        }

        if (provider.Equals(AzureKeyVaultProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return await ValidateAzureKeyVaultReferenceAsync(normalizedName, credential, references, cancellationToken)
                .ConfigureAwait(false);
        }

        return Failure($"Credential provider '{provider}' enrollment is not implemented in this build.", references);
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
        if (provider.Equals(DpapiFileProviderName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var password = DpapiCredentialFileStore.ReadPassword(
                    normalizedName,
                    credential["Username"]!,
                    credential["Path"]!);
                return Task.FromResult(Success($"Credential reference '{normalizedName}' has a readable DPAPI-protected credential file.", references));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or InvalidOperationException or PlatformNotSupportedException)
            {
                return Task.FromResult(Failure($"Credential reference '{normalizedName}' DPAPI credential file is not usable. {exception.Message}", references));
            }
        }

        if (provider.Equals(WindowsCredentialManagerProviderName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var password = WindowsCredentialManagerStore.ReadPassword(
                    credential["Target"]!,
                    credential["Username"]!);
                return Task.FromResult(Success($"Credential reference '{normalizedName}' has a readable Windows Credential Manager target.", references));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or InvalidOperationException or PlatformNotSupportedException)
            {
                return Task.FromResult(Failure($"Credential reference '{normalizedName}' Windows Credential Manager target is not usable. {exception.Message}", references));
            }
        }

        if (provider.Equals(AzureKeyVaultProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateAzureKeyVaultReferenceAsync(normalizedName, credential, references, cancellationToken);
        }

        if (!provider.Equals(DpapiFileProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Success($"Credential reference '{normalizedName}' is defined with provider '{provider}'.", references));
        }

        return Task.FromResult(Success($"Credential reference '{normalizedName}' is defined with provider '{provider}'.", references));
    }

    public Task<CredentialProviderOperationResult> RemoveAsync(
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

        if (!validation.ProviderName.Equals(DpapiFileProviderName, StringComparison.OrdinalIgnoreCase))
        {
            if (!validation.ProviderName.Equals(WindowsCredentialManagerProviderName, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Failure(
                    "Config-defined credential references must be removed from Dispatch config.",
                    references));
            }

            try
            {
                WindowsCredentialManagerStore.Delete(credential["Target"]!);
                return Task.FromResult(Success($"Windows Credential Manager target for '{normalizedName}' was removed. The config reference remains until removed from Dispatch config.", references));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return Task.FromResult(Failure($"Windows Credential Manager target for '{normalizedName}' could not be removed. {exception.Message}", references));
            }
        }

        try
        {
            DpapiCredentialFileStore.Delete(credential["Path"]!);
            return Task.FromResult(Success($"DPAPI credential file for '{normalizedName}' was removed. The config reference remains until removed from Dispatch config.", references));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Failure($"DPAPI credential file for '{normalizedName}' could not be removed. {exception.Message}", references));
        }
    }

    private async Task<CredentialProviderOperationResult> ValidateAzureKeyVaultReferenceAsync(
        string name,
        IConfigurationSection credential,
        IReadOnlyList<CredentialReference> references,
        CancellationToken cancellationToken)
    {
        var result = await (azureKeyVaultSecrets ?? new AzureKeyVaultCredentialSecretResolver())
            .ResolveSecretAsync(CreateAzureKeyVaultRequest(name, credential), cancellationToken)
            .ConfigureAwait(false);
        result.Secret?.Dispose();

        return result.Succeeded
            ? Success($"Azure Key Vault credential reference '{name}' is reachable and the configured secret can be read. No local secret was stored.", references)
            : Failure($"Azure Key Vault credential reference '{name}' is not usable. {result.Message}", references);
    }

    private async Task<CredentialProviderOperationResult> EnrollWindowsCredentialManagerAsync(
        string name,
        IConfigurationSection credential,
        bool force,
        IReadOnlyList<CredentialReference> references,
        CancellationToken cancellationToken)
    {
        var target = credential["Target"]!;
        if (!force && WindowsCredentialManagerStore.Exists(target))
        {
            return Failure($"Windows Credential Manager target '{target}' already exists. Use --force to overwrite it.", references);
        }

        if (prompt is null)
        {
            return Failure("Windows Credential Manager enrollment requires an interactive secure password prompt.", references);
        }

        SecureString? password = null;
        SecureString? confirmation = null;
        try
        {
            var username = credential["Username"]!;
            password = await prompt.PromptForPasswordAsync(
                    new RuntimeCredentialPromptRequest(name, username, WindowsCredentialManagerProviderName, "Password"),
                    cancellationToken)
                .ConfigureAwait(false);
            confirmation = await prompt.PromptForPasswordAsync(
                    new RuntimeCredentialPromptRequest(name, username, WindowsCredentialManagerProviderName, "Confirm password"),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!WindowsCredentialManagerStore.PasswordsEqual(password, confirmation))
            {
                return Failure("Password confirmation did not match. No Windows Credential Manager credential was written.", references);
            }

            WindowsCredentialManagerStore.Write(target, username, password, force);
            return Success($"Windows Credential Manager target for '{name}' was written to '{target}'.", references);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return Failure($"Windows Credential Manager enrollment failed for '{name}'. {exception.Message}", references);
        }
        finally
        {
            password?.Dispose();
            confirmation?.Dispose();
        }
    }

    private async Task<CredentialProviderOperationResult> EnrollDpapiFileAsync(
        string name,
        IConfigurationSection credential,
        bool force,
        IReadOnlyList<CredentialReference> references,
        CancellationToken cancellationToken)
    {
        var path = credential["Path"]!;
        if (File.Exists(path) && !force)
        {
            return Failure($"DPAPI credential file '{Path.GetFullPath(path)}' already exists. Use --force to overwrite it.", references);
        }

        if (prompt is null)
        {
            return Failure("DPAPI credential enrollment requires an interactive secure password prompt.", references);
        }

        SecureString? password = null;
        SecureString? confirmation = null;
        try
        {
            var username = credential["Username"]!;
            password = await prompt.PromptForPasswordAsync(
                    new RuntimeCredentialPromptRequest(name, username, DpapiFileProviderName, "Password"),
                    cancellationToken)
                .ConfigureAwait(false);
            confirmation = await prompt.PromptForPasswordAsync(
                    new RuntimeCredentialPromptRequest(name, username, DpapiFileProviderName, "Confirm password"),
                    cancellationToken)
                .ConfigureAwait(false);

            var enteredPassword = password;
            var confirmedPassword = confirmation;
            if (!DpapiCredentialFileStore.PasswordsEqual(enteredPassword, confirmedPassword))
            {
                return Failure("Password confirmation did not match. No credential file was written.", references);
            }

            DpapiCredentialFileStore.Write(name, username, path, enteredPassword, force);
            return Success($"DPAPI credential file for '{name}' was written to '{Path.GetFullPath(path)}'.", references);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return Failure($"DPAPI credential enrollment failed for '{name}'. {exception.Message}", references);
        }
        finally
        {
            password?.Dispose();
            confirmation?.Dispose();
        }
    }

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

    private static AzureKeyVaultSecretRequest CreateAzureKeyVaultRequest(
        string name,
        IConfigurationSection credential) =>
        new(
            name,
            credential["Username"]!,
            new Uri(credential["VaultUri"]!),
            credential["SecretName"]!,
            credential["Auth"]!,
            NormalizeOptionalValue(credential["ManagedIdentityClientId"]));

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
