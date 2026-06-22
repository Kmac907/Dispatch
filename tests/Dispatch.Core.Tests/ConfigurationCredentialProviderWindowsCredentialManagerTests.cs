using Dispatch.Core.Configuration;
using Dispatch.Core.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
using System.Security;

namespace Dispatch.Core.Tests;

public sealed class ConfigurationCredentialProviderWindowsCredentialManagerTests
{
    [Fact]
    public async Task WindowsCredentialManagerAddTestAndRemoveManageCredentialTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var target = CreateCredentialTarget();
        var provider = CreateProvider(
            target,
            new RecordingRuntimeCredentialPrompt("secret-value", "secret-value"));

        try
        {
            var add = await provider.AddAsync(
                new CredentialAddRequest("domain-admin", null),
                CancellationToken.None);

            Assert.True(add.Succeeded, add.Message);
            Assert.True(WindowsCredentialManagerStore.Exists(target));

            var test = await provider.TestAsync(
                new CredentialReferenceRequest("domain-admin"),
                CancellationToken.None);
            Assert.True(test.Succeeded, test.Message);
            Assert.Contains("readable Windows Credential Manager", test.Message);

            var secondAdd = await provider.AddAsync(
                new CredentialAddRequest("domain-admin", null),
                CancellationToken.None);
            Assert.False(secondAdd.Succeeded);
            Assert.Contains("--force", secondAdd.Message);

            var remove = await provider.RemoveAsync(
                new CredentialReferenceRequest("domain-admin"),
                CancellationToken.None);
            Assert.True(remove.Succeeded, remove.Message);
            Assert.False(WindowsCredentialManagerStore.Exists(target));
        }
        finally
        {
            WindowsCredentialManagerStore.Delete(target);
        }
    }

    [Fact]
    public async Task WindowsCredentialManagerAddRejectsMismatchedConfirmation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var target = CreateCredentialTarget();
        var provider = CreateProvider(
            target,
            new RecordingRuntimeCredentialPrompt("secret-value", "different-value"));

        try
        {
            var add = await provider.AddAsync(
                new CredentialAddRequest("domain-admin", null),
                CancellationToken.None);

            Assert.False(add.Succeeded);
            Assert.Contains("confirmation", add.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(WindowsCredentialManagerStore.Exists(target));
        }
        finally
        {
            WindowsCredentialManagerStore.Delete(target);
        }
    }

    [Fact]
    public async Task RuntimeResolverResolvesWindowsCredentialManagerCredential()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var target = CreateCredentialTarget();
        var configuration = CreateConfiguration(target);
        var provider = new ConfigurationCredentialProvider(
            configuration,
            Options.Create(new DispatchOptions { CredentialProvider = "prompt" }),
            new RecordingRuntimeCredentialPrompt("secret-value", "secret-value"));

        try
        {
            var add = await provider.AddAsync(
                new CredentialAddRequest("domain-admin", null),
                CancellationToken.None);
            Assert.True(add.Succeeded, add.Message);

            var resolver = new ConfigurationRuntimeCredentialResolver(
                configuration,
                Options.Create(new DispatchOptions { CredentialProvider = "prompt" }),
                new RecordingRuntimeCredentialPrompt("unused"));

            var result = await resolver.ResolveAsync(["domain-admin"], CancellationToken.None);

            Assert.True(result.Succeeded, result.FailureMessage);
            var credential = Assert.Single(result.Credentials.Values);
            Assert.Equal("domain-admin", credential.ReferenceName);
            Assert.Equal(@"SCF\domain.admin", credential.UserName);
            Assert.Equal("windows_credential_manager", credential.ProviderName);
            Assert.Equal("secret-value", ToPlainText(credential.Password));
        }
        finally
        {
            WindowsCredentialManagerStore.Delete(target);
        }
    }

    private static ConfigurationCredentialProvider CreateProvider(
        string target,
        IRuntimeCredentialPrompt prompt) =>
        new(
            CreateConfiguration(target),
            Options.Create(new DispatchOptions { CredentialProvider = "prompt" }),
            prompt);

    private static IConfigurationRoot CreateConfiguration(string target) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dispatch:CredentialProvider"] = "prompt",
                ["Credentials:domain-admin:Provider"] = "windows_credential_manager",
                ["Credentials:domain-admin:Username"] = @"SCF\domain.admin",
                ["Credentials:domain-admin:Target"] = target
            })
            .Build();

    private static string CreateCredentialTarget() =>
        $"Dispatch/Tests/{Guid.NewGuid():N}";

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

    private sealed class RecordingRuntimeCredentialPrompt(params string[] passwords) : IRuntimeCredentialPrompt
    {
        private readonly Queue<string> passwords = new(passwords);

        public Task<SecureString> PromptForPasswordAsync(
            RuntimeCredentialPromptRequest request,
            CancellationToken cancellationToken)
        {
            var password = passwords.Count == 0 ? string.Empty : passwords.Dequeue();
            var secureString = new SecureString();
            foreach (var character in password)
            {
                secureString.AppendChar(character);
            }

            return Task.FromResult(secureString);
        }
    }
}
