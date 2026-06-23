using Dispatch.Core.Configuration;
using Dispatch.Core.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
using System.Security;

namespace Dispatch.Core.Tests;

public sealed class ConfigurationCredentialProviderAzureKeyVaultTests
{
    [Fact]
    public async Task AzureKeyVaultAddAndTestValidateSecretReferenceWithoutLocalStorage()
    {
        var secretResolver = new RecordingAzureKeyVaultSecretResolver("secret-value");
        var provider = new ConfigurationCredentialProvider(
            CreateConfiguration(),
            Options.Create(new DispatchOptions { CredentialProvider = "prompt" }),
            prompt: null,
            azureKeyVaultSecrets: secretResolver);

        var add = await provider.AddAsync(
            new CredentialAddRequest("kv-prod-admin", null),
            CancellationToken.None);
        var test = await provider.TestAsync(
            new CredentialReferenceRequest("kv-prod-admin"),
            CancellationToken.None);

        Assert.True(add.Succeeded, add.Message);
        Assert.Contains("No local secret was stored", add.Message);
        Assert.True(test.Succeeded, test.Message);
        Assert.Equal(2, secretResolver.Requests.Count);
        Assert.All(secretResolver.Requests, request =>
        {
            Assert.Equal("kv-prod-admin", request.ReferenceName);
            Assert.Equal(@"CONTOSO\prod.admin", request.UserName);
            Assert.Equal(new Uri("https://contoso-dispatch-kv.vault.azure.net/"), request.VaultUri);
            Assert.Equal("prod-admin-password", request.SecretName);
            Assert.Equal("default_azure_credential", request.Auth);
        });
    }

    [Fact]
    public async Task RuntimeResolverResolvesAzureKeyVaultCredential()
    {
        var secretResolver = new RecordingAzureKeyVaultSecretResolver("secret-value");
        var resolver = new ConfigurationRuntimeCredentialResolver(
            CreateConfiguration(),
            Options.Create(new DispatchOptions { CredentialProvider = "prompt" }),
            new UnavailableRuntimeCredentialPrompt(),
            secretResolver);

        var result = await resolver.ResolveAsync(["kv-prod-admin"], CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureMessage);
        var credential = Assert.Single(result.Credentials.Values);
        Assert.Equal("kv-prod-admin", credential.ReferenceName);
        Assert.Equal(@"CONTOSO\prod.admin", credential.UserName);
        Assert.Equal("azure_keyvault", credential.ProviderName);
        Assert.Equal("secret-value", ToPlainText(credential.Password));
        var request = Assert.Single(secretResolver.Requests);
        Assert.Equal("prod-admin-password", request.SecretName);
    }

    [Fact]
    public async Task AzureKeyVaultTestReportsResolverFailure()
    {
        var provider = new ConfigurationCredentialProvider(
            CreateConfiguration(),
            Options.Create(new DispatchOptions { CredentialProvider = "prompt" }),
            prompt: null,
            azureKeyVaultSecrets: new RecordingAzureKeyVaultSecretResolver("secret-value", shouldFail: true));

        var test = await provider.TestAsync(
            new CredentialReferenceRequest("kv-prod-admin"),
            CancellationToken.None);

        Assert.False(test.Succeeded);
        Assert.Contains("not usable", test.Message);
        Assert.DoesNotContain("secret-value", test.Message);
    }

    private static IConfigurationRoot CreateConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dispatch:CredentialProvider"] = "prompt",
                ["Credentials:kv-prod-admin:Provider"] = "azure_keyvault",
                ["Credentials:kv-prod-admin:Username"] = @"CONTOSO\prod.admin",
                ["Credentials:kv-prod-admin:VaultUri"] = "https://contoso-dispatch-kv.vault.azure.net/",
                ["Credentials:kv-prod-admin:SecretName"] = "prod-admin-password",
                ["Credentials:kv-prod-admin:Auth"] = "default_azure_credential"
            })
            .Build();

    private static string ToPlainText(SecureString secureString)
    {
        var pointer = IntPtr.Zero;
        try
        {
            pointer = Marshal.SecureStringToBSTR(secureString);
            return Marshal.PtrToStringBSTR(pointer) ?? string.Empty;
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.ZeroFreeBSTR(pointer);
            }
        }
    }

    private sealed class RecordingAzureKeyVaultSecretResolver(
        string secret,
        bool shouldFail = false) : IAzureKeyVaultCredentialSecretResolver
    {
        public List<AzureKeyVaultSecretRequest> Requests { get; } = [];

        public Task<AzureKeyVaultSecretResult> ResolveSecretAsync(
            AzureKeyVaultSecretRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (shouldFail)
            {
                return Task.FromResult(AzureKeyVaultSecretResult.Failure("The configured secret could not be read."));
            }

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
