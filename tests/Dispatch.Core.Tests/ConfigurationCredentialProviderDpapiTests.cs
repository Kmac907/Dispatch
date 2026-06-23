using Dispatch.Core.Configuration;
using Dispatch.Core.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Dispatch.Core.Tests;

public sealed class ConfigurationCredentialProviderDpapiTests
{
    [Fact]
    public async Task DpapiFileAddTestAndRemoveManageProtectedCredentialFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTemporaryDirectory();
        var credentialPath = Path.Combine(root, "helpdesk-local.cred");
        var provider = CreateProvider(
            credentialPath,
            new RecordingRuntimeCredentialPrompt("secret-value", "secret-value"));

        try
        {
            var add = await provider.AddAsync(
                new CredentialAddRequest("helpdesk-local", null),
                CancellationToken.None);

            Assert.True(add.Succeeded, add.Message);
            Assert.True(File.Exists(credentialPath));
            var persisted = await File.ReadAllTextAsync(credentialPath);
            Assert.Contains("dpapi_file", persisted);
            Assert.Contains("helpdesk-local", persisted);
            Assert.Contains(@".\\helpdesk-admin", persisted);
            Assert.DoesNotContain("secret-value", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", persisted, StringComparison.OrdinalIgnoreCase);

            var test = await provider.TestAsync(
                new CredentialReferenceRequest("helpdesk-local"),
                CancellationToken.None);
            Assert.True(test.Succeeded, test.Message);
            Assert.Contains("readable DPAPI-protected", test.Message);

            var secondAdd = await provider.AddAsync(
                new CredentialAddRequest("helpdesk-local", null),
                CancellationToken.None);
            Assert.False(secondAdd.Succeeded);
            Assert.Contains("--force", secondAdd.Message);

            var remove = await provider.RemoveAsync(
                new CredentialReferenceRequest("helpdesk-local"),
                CancellationToken.None);
            Assert.True(remove.Succeeded, remove.Message);
            Assert.False(File.Exists(credentialPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DpapiFileAddRejectsMismatchedConfirmation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTemporaryDirectory();
        var credentialPath = Path.Combine(root, "helpdesk-local.cred");
        var provider = CreateProvider(
            credentialPath,
            new RecordingRuntimeCredentialPrompt("secret-value", "different-value"));

        try
        {
            var add = await provider.AddAsync(
                new CredentialAddRequest("helpdesk-local", null),
                CancellationToken.None);

            Assert.False(add.Succeeded);
            Assert.Contains("confirmation", add.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(credentialPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DpapiFileAddRestrictsCredentialFileAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTemporaryDirectory();
        var credentialPath = Path.Combine(root, "helpdesk-local.cred");
        var provider = CreateProvider(
            credentialPath,
            new RecordingRuntimeCredentialPrompt("secret-value", "secret-value"));

        try
        {
            var add = await provider.AddAsync(
                new CredentialAddRequest("helpdesk-local", null),
                CancellationToken.None);

            Assert.True(add.Succeeded, add.Message);

            var fileSecurity = new FileInfo(credentialPath).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            Assert.True(fileSecurity.AreAccessRulesProtected);

            var currentUser = WindowsIdentity.GetCurrent().User!;
            var allowedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                currentUser.Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value
            };

            var rules = fileSecurity
                .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .ToArray();

            Assert.NotEmpty(rules);
            Assert.All(rules, rule =>
            {
                Assert.False(rule.IsInherited);
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                var sid = Assert.IsType<SecurityIdentifier>(rule.IdentityReference);
                Assert.Contains(sid.Value, allowedSids);
                Assert.True(rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
            });

            Assert.Contains(rules, rule => ((SecurityIdentifier)rule.IdentityReference).Value.Equals(currentUser.Value, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeResolverResolvesDpapiFileCredential()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTemporaryDirectory();
        var credentialPath = Path.Combine(root, "helpdesk-local.cred");
        var prompt = new RecordingRuntimeCredentialPrompt("secret-value", "secret-value");
        var configuration = CreateConfiguration(credentialPath);
        var provider = new ConfigurationCredentialProvider(
            configuration,
            Options.Create(new DispatchOptions { CredentialProvider = "prompt" }),
            prompt);

        try
        {
            var add = await provider.AddAsync(
                new CredentialAddRequest("helpdesk-local", null),
                CancellationToken.None);
            Assert.True(add.Succeeded, add.Message);

            var resolver = new ConfigurationRuntimeCredentialResolver(
                configuration,
                Options.Create(new DispatchOptions { CredentialProvider = "prompt" }),
                new RecordingRuntimeCredentialPrompt("unused"));

            var result = await resolver.ResolveAsync(["helpdesk-local"], CancellationToken.None);

            Assert.True(result.Succeeded, result.FailureMessage);
            var credential = Assert.Single(result.Credentials.Values);
            Assert.Equal("helpdesk-local", credential.ReferenceName);
            Assert.Equal(@".\helpdesk-admin", credential.UserName);
            Assert.Equal("dpapi_file", credential.ProviderName);
            Assert.Equal("secret-value", ToPlainText(credential.Password));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ConfigurationCredentialProvider CreateProvider(
        string credentialPath,
        IRuntimeCredentialPrompt prompt) =>
        new(
            CreateConfiguration(credentialPath),
            Options.Create(new DispatchOptions { CredentialProvider = "prompt" }),
            prompt);

    private static IConfigurationRoot CreateConfiguration(string credentialPath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dispatch:CredentialProvider"] = "prompt",
                ["Credentials:helpdesk-local:Provider"] = "dpapi_file",
                ["Credentials:helpdesk-local:Username"] = @".\helpdesk-admin",
                ["Credentials:helpdesk-local:Path"] = credentialPath
            })
            .Build();

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dispatch-dpapi-creds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

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
