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
    public async Task ResolveAsyncRejectsLaterProtectedProvidersUntilTheirRuntimeSlices()
    {
        var resolver = CreateResolver(
            new Dictionary<string, string?>
            {
                ["Credentials:domain-admin:Provider"] = "windows_credential_manager",
                ["Credentials:domain-admin:Username"] = @"SCF\domain.admin",
                ["Credentials:domain-admin:Target"] = "Dispatch/domain-admin"
            },
            new RecordingRuntimeCredentialPrompt("secret-value"));

        var result = await resolver.ResolveAsync(["domain-admin"], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("runtime resolution is not implemented", result.FailureMessage);
        Assert.Empty(result.Credentials);
    }

    private static ConfigurationRuntimeCredentialResolver CreateResolver(
        IReadOnlyDictionary<string, string?> values,
        IRuntimeCredentialPrompt prompt)
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
            prompt);
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
}
