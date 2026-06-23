using Dispatch.Core.Configuration;
using Dispatch.Core.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Security;

namespace Dispatch.Core.Tests;

public sealed class ConfigurationRuntimeCredentialResolverTests
{
    [Fact]
    public async Task ResolveAsyncPromptsForPromptProviderCredential()
    {
        var prompt = new RecordingRuntimeCredentialPrompt("secret-value");
        var resolver = CreateResolver(
            new Dictionary<string, string?>
            {
                ["Dispatch:CredentialProvider"] = "prompt",
                ["Credentials:prod-admin:Provider"] = "prompt",
                ["Credentials:prod-admin:Username"] = @"SCF\prod.admin"
            },
            prompt);

        var result = await resolver.ResolveAsync(["prod-admin"], CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureMessage);
        var credential = Assert.Single(result.Credentials.Values);
        Assert.Equal("prod-admin", credential.ReferenceName);
        Assert.Equal(@"SCF\prod.admin", credential.UserName);
        Assert.Equal("prompt", credential.ProviderName);
        var promptRequest = Assert.Single(prompt.Requests);
        Assert.Equal("prod-admin", promptRequest.ReferenceName);
        Assert.True(credential.Password.IsReadOnly());
    }

    [Fact]
    public async Task ResolveAsyncRejectsPowerShellWrapperOnlyProviderFromDirectRuntime()
    {
        var resolver = CreateResolver(
            new Dictionary<string, string?>
            {
                ["Credentials:prod-admin:Provider"] = "pscredential",
                ["Credentials:prod-admin:Username"] = @"SCF\prod.admin"
            },
            new RecordingRuntimeCredentialPrompt("secret-value"));

        var result = await resolver.ResolveAsync(["prod-admin"], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("only valid through the PowerShell wrapper", result.FailureMessage);
        Assert.Empty(result.Credentials);
    }

    [Fact]
    public async Task ResolveAsyncResolvesAzureKeyVaultCredential()
    {
        var secretResolver = new RecordingAzureKeyVaultSecretResolver("secret-value");
        var resolver = CreateResolver(
            new Dictionary<string, string?>
            {
                ["Credentials:kv-prod-admin:Provider"] = "azure_keyvault",
                ["Credentials:kv-prod-admin:Username"] = @"CONTOSO\prod.admin",
                ["Credentials:kv-prod-admin:VaultUri"] = "https://contoso-dispatch-kv.vault.azure.net/",
                ["Credentials:kv-prod-admin:SecretName"] = "prod-admin-password",
                ["Credentials:kv-prod-admin:Auth"] = "default_azure_credential"
            },
            new RecordingRuntimeCredentialPrompt("unused"),
            secretResolver);

        var result = await resolver.ResolveAsync(["kv-prod-admin"], CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureMessage);
        var credential = Assert.Single(result.Credentials.Values);
        Assert.Equal("kv-prod-admin", credential.ReferenceName);
        Assert.Equal(@"CONTOSO\prod.admin", credential.UserName);
        Assert.Equal("azure_keyvault", credential.ProviderName);
        var request = Assert.Single(secretResolver.Requests);
        Assert.Equal("prod-admin-password", request.SecretName);
    }

    private static ConfigurationRuntimeCredentialResolver CreateResolver(
        IReadOnlyDictionary<string, string?> values,
        IRuntimeCredentialPrompt prompt,
        IAzureKeyVaultCredentialSecretResolver? azureKeyVaultSecrets = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new ConfigurationRuntimeCredentialResolver(
            configuration,
            Options.Create(new DispatchOptions
            {
                CredentialProvider = configuration["Dispatch:CredentialProvider"] ?? "prompt"
            }),
            prompt,
            azureKeyVaultSecrets);
    }

    private sealed class RecordingRuntimeCredentialPrompt(string password) : IRuntimeCredentialPrompt
    {
        public List<RuntimeCredentialPromptRequest> Requests { get; } = [];

        public Task<SecureString> PromptForPasswordAsync(
            RuntimeCredentialPromptRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var secureString = new SecureString();
            foreach (var character in password)
            {
                secureString.AppendChar(character);
            }

            return Task.FromResult(secureString);
        }
    }

    private sealed class RecordingAzureKeyVaultSecretResolver(string secret) : IAzureKeyVaultCredentialSecretResolver
    {
        public List<AzureKeyVaultSecretRequest> Requests { get; } = [];

        public Task<AzureKeyVaultSecretResult> ResolveSecretAsync(
            AzureKeyVaultSecretRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var secureString = new SecureString();
            foreach (var character in secret)
            {
                secureString.AppendChar(character);
            }

            secureString.MakeReadOnly();
            return Task.FromResult(AzureKeyVaultSecretResult.Success(secureString));
        }
    }
}
