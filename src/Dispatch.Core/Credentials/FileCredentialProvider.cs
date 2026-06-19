using Dispatch.Core.Configuration;
using Dispatch.Core.Defaults;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dispatch.Core.Credentials;

public sealed class FileCredentialProvider(IOptions<DispatchOptions> options) : ICredentialProvider
{
    public const string ProviderName = "file";

    private static readonly JsonSerializerOptions SerializerOptions = new(DispatchJson.Options)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = false
    };

    private readonly SemaphoreSlim gate = new(1, 1);

    private string StorePath => string.IsNullOrWhiteSpace(options.Value.CredentialStorePath)
        ? DispatchDefaults.CredentialStorePath
        : options.Value.CredentialStorePath.Trim();

    public Task<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new CredentialProviderStatus(
            ProviderName,
            IsAvailable: true,
            $"Credential references are stored in '{StorePath}'. No plaintext secrets are stored."));

    public async Task<CredentialProviderOperationResult> AddAsync(
        CredentialAddRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedName = NormalizeName(request.Name);
        if (normalizedName is null)
        {
            return Failure("Credential name is required.", []);
        }

        return await WithStoreAsync(async store =>
        {
            store.References.RemoveAll(reference => reference.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
            store.References.Add(new StoredCredentialReference(normalizedName, NormalizeOptionalValue(request.UserName)));
            SortReferences(store);
            await WriteStoreAsync(store, cancellationToken).ConfigureAwait(false);
            return Success("Credential reference added.", ToReferences(store));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CredentialProviderOperationResult> ListAsync(CancellationToken cancellationToken) =>
        await WithStoreAsync(
            store => Task.FromResult(Success("Credential references listed.", ToReferences(store))),
            cancellationToken).ConfigureAwait(false);

    public async Task<CredentialProviderOperationResult> TestAsync(
        CredentialReferenceRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedName = NormalizeName(request.Name);
        if (normalizedName is null)
        {
            return Failure("Credential name is required.", []);
        }

        return await WithStoreAsync(store =>
        {
            var references = ToReferences(store);
            var exists = references.Any(reference => reference.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(exists
                ? Success("Credential reference is available.", references)
                : Failure($"Credential reference '{normalizedName}' was not found.", references));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CredentialProviderOperationResult> RemoveAsync(
        CredentialReferenceRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedName = NormalizeName(request.Name);
        if (normalizedName is null)
        {
            return Failure("Credential name is required.", []);
        }

        return await WithStoreAsync(async store =>
        {
            var removed = store.References.RemoveAll(reference => reference.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return Failure($"Credential reference '{normalizedName}' was not found.", ToReferences(store));
            }

            await WriteStoreAsync(store, cancellationToken).ConfigureAwait(false);
            return Success("Credential reference removed.", ToReferences(store));
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CredentialProviderOperationResult> WithStoreAsync(
        Func<CredentialReferenceStore, Task<CredentialProviderOperationResult>> operation,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = await ReadStoreAsync(cancellationToken).ConfigureAwait(false);
            return await operation(store).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            return Failure($"Credential reference store '{StorePath}' is invalid JSON. {exception.Message}", []);
        }
        catch (IOException exception)
        {
            return Failure($"Credential reference store '{StorePath}' is unavailable. {exception.Message}", []);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure($"Credential reference store '{StorePath}' is unavailable. {exception.Message}", []);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CredentialReferenceStore> ReadStoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StorePath))
        {
            return new CredentialReferenceStore();
        }

        await using var stream = File.OpenRead(StorePath);
        var store = await JsonSerializer
            .DeserializeAsync<CredentialReferenceStore>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
        store ??= new CredentialReferenceStore();
        store.References ??= [];
        SortReferences(store);
        return store;
    }

    private async Task WriteStoreAsync(CredentialReferenceStore store, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{StorePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, store, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (File.Exists(StorePath))
            {
                File.Copy(temporaryPath, StorePath, overwrite: true);
                File.Delete(temporaryPath);
            }
            else
            {
                File.Move(temporaryPath, StorePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static IReadOnlyList<CredentialReference> ToReferences(CredentialReferenceStore store) =>
        store.References
            .OrderBy(static reference => reference.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static reference => new CredentialReference(reference.Name, reference.UserName))
            .ToArray();

    private static void SortReferences(CredentialReferenceStore store) =>
        store.References.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

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

    private sealed class CredentialReferenceStore
    {
        public List<StoredCredentialReference> References { get; set; } = [];
    }

    private sealed record StoredCredentialReference(string Name, string? UserName);
}
