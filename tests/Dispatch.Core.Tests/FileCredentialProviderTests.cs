using Dispatch.Core.Configuration;
using Dispatch.Core.Credentials;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Dispatch.Core.Tests;

public sealed class FileCredentialProviderTests
{
    [Fact]
    public async Task AddListTestAndRemovePersistReferenceMetadataWithoutSecrets()
    {
        var root = CreateTemporaryDirectory();
        var storePath = Path.Combine(root, "references.json");
        var provider = CreateProvider(storePath);

        try
        {
            var add = await provider.AddAsync(
                new CredentialAddRequest("prod-admin", @"CONTOSO\Admin"),
                CancellationToken.None);
            Assert.True(add.Succeeded, add.Message);
            Assert.True(add.ProviderAvailable);
            Assert.Equal(FileCredentialProvider.ProviderName, add.ProviderName);

            var list = await provider.ListAsync(CancellationToken.None);
            var reference = Assert.Single(list.References);
            Assert.Equal("prod-admin", reference.Name);
            Assert.Equal(@"CONTOSO\Admin", reference.UserName);

            var test = await provider.TestAsync(
                new CredentialReferenceRequest("PROD-ADMIN"),
                CancellationToken.None);
            Assert.True(test.Succeeded, test.Message);

            var persisted = await File.ReadAllTextAsync(storePath);
            Assert.Contains("prod-admin", persisted);
            Assert.Contains(@"CONTOSO\\Admin", persisted);
            Assert.DoesNotContain("password", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", persisted, StringComparison.OrdinalIgnoreCase);
            using var json = JsonDocument.Parse(persisted);
            Assert.True(json.RootElement.TryGetProperty("references", out var references));
            Assert.False(references[0].TryGetProperty("password", out _));

            var remove = await provider.RemoveAsync(
                new CredentialReferenceRequest("prod-admin"),
                CancellationToken.None);
            Assert.True(remove.Succeeded, remove.Message);
            Assert.Empty(remove.References);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingReferenceFailsWithProviderAvailable()
    {
        var root = CreateTemporaryDirectory();
        var provider = CreateProvider(Path.Combine(root, "references.json"));

        try
        {
            var result = await provider.TestAsync(
                new CredentialReferenceRequest("missing"),
                CancellationToken.None);

            Assert.True(result.ProviderAvailable);
            Assert.False(result.Succeeded);
            Assert.Contains("was not found", result.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FileCredentialProvider CreateProvider(string storePath) =>
        new(Options.Create(new DispatchOptions
        {
            CredentialProvider = "file",
            CredentialStorePath = storePath
        }));

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dispatch-creds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
