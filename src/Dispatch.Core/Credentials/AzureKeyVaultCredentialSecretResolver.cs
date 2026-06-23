using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using System.Security;

namespace Dispatch.Core.Credentials;

public sealed record AzureKeyVaultSecretRequest(
    string ReferenceName,
    string UserName,
    Uri VaultUri,
    string SecretName,
    string Auth,
    string? ManagedIdentityClientId = null);

public sealed record AzureKeyVaultSecretResult(
    bool Succeeded,
    SecureString? Secret,
    string Message)
{
    public static AzureKeyVaultSecretResult Success(SecureString secret) =>
        new(true, secret, "Azure Key Vault secret was read.");

    public static AzureKeyVaultSecretResult Failure(string message) =>
        new(false, null, message);
}

public interface IAzureKeyVaultCredentialSecretResolver
{
    Task<AzureKeyVaultSecretResult> ResolveSecretAsync(
        AzureKeyVaultSecretRequest request,
        CancellationToken cancellationToken);
}

public sealed class AzureKeyVaultCredentialSecretResolver : IAzureKeyVaultCredentialSecretResolver
{
    public async Task<AzureKeyVaultSecretResult> ResolveSecretAsync(
        AzureKeyVaultSecretRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var credential = CreateCredential(request);
            var client = new SecretClient(request.VaultUri, credential);
            var response = await client.GetSecretAsync(request.SecretName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var value = response.Value.Value;
            if (string.IsNullOrEmpty(value))
            {
                return AzureKeyVaultSecretResult.Failure(
                    $"Azure Key Vault secret '{request.SecretName}' is empty.");
            }

            var secret = new SecureString();
            foreach (var character in value)
            {
                secret.AppendChar(character);
            }

            secret.MakeReadOnly();
            return AzureKeyVaultSecretResult.Success(secret);
        }
        catch (Exception exception) when (exception is RequestFailedException or AuthenticationFailedException or InvalidOperationException or UriFormatException or AggregateException)
        {
            return AzureKeyVaultSecretResult.Failure(exception.Message);
        }
    }

    private static TokenCredential CreateCredential(AzureKeyVaultSecretRequest request) =>
        request.Auth.ToLowerInvariant() switch
        {
            "default_azure_credential" => new DefaultAzureCredential(),
            "managed_identity" when !string.IsNullOrWhiteSpace(request.ManagedIdentityClientId) =>
                new ManagedIdentityCredential(request.ManagedIdentityClientId.Trim()),
            "managed_identity" => new ManagedIdentityCredential(),
            "azure_cli" => new AzureCliCredential(),
            _ => throw new InvalidOperationException(
                $"Unsupported Azure Key Vault auth mode '{request.Auth}'.")
        };
}
