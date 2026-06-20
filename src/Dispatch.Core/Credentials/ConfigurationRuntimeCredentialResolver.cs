using Dispatch.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Security;

namespace Dispatch.Core.Credentials;

public sealed class ConfigurationRuntimeCredentialResolver(
    IConfiguration configuration,
    IOptions<DispatchOptions> options,
    IRuntimeCredentialPrompt prompt) : IRuntimeCredentialResolver
{
    private const string CredentialsSectionName = "Credentials";
    private const string PromptProviderName = "prompt";
    private const string PsCredentialProviderName = "pscredential";

    public async Task<RuntimeCredentialResolutionResult> ResolveAsync(
        IEnumerable<string> credentialReferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentialReferences);

        var references = credentialReferences
            .Where(static reference => !string.IsNullOrWhiteSpace(reference))
            .Select(static reference => reference.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (references.Length == 0)
        {
            return RuntimeCredentialResolutionResult.Success(
                new Dictionary<string, DispatchResolvedCredential>(StringComparer.OrdinalIgnoreCase));
        }

        var resolved = new Dictionary<string, DispatchResolvedCredential>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var reference in references)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var credential = configuration.GetSection($"{CredentialsSectionName}:{reference}");
                if (!credential.Exists())
                {
                    return FailAndDispose(
                        resolved,
                        $"Credential reference '{reference}' is not defined in Dispatch config.");
                }

                var providerName = ResolveProvider(credential);
                if (providerName.Equals(PsCredentialProviderName, StringComparison.OrdinalIgnoreCase))
                {
                    return FailAndDispose(
                        resolved,
                        $"Credential reference '{reference}' uses provider 'pscredential', which is only valid through the PowerShell wrapper.");
                }

                if (!providerName.Equals(PromptProviderName, StringComparison.OrdinalIgnoreCase))
                {
                    return FailAndDispose(
                        resolved,
                        $"Credential provider '{providerName}' runtime resolution is not implemented in this build. Prompt-provider PSRP handoff is the current supported slice.");
                }

                var username = NormalizeOptionalValue(credential["Username"]);
                if (string.IsNullOrWhiteSpace(username))
                {
                    return FailAndDispose(
                        resolved,
                        $"Credential reference '{reference}' is missing required field 'username'.");
                }

                var password = await prompt.PromptForPasswordAsync(
                        new RuntimeCredentialPromptRequest(reference, username, providerName),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (password.Length == 0)
                {
                    password.Dispose();
                    return FailAndDispose(
                        resolved,
                        $"Credential reference '{reference}' did not receive a password.");
                }

                password.MakeReadOnly();
                resolved[reference] = new DispatchResolvedCredential(reference, username, providerName, password);
            }
        }
        catch (OperationCanceledException)
        {
            DisposeAll(resolved);
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return FailAndDispose(resolved, exception.Message);
        }

        return RuntimeCredentialResolutionResult.Success(resolved);
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

    private static RuntimeCredentialResolutionResult FailAndDispose(
        Dictionary<string, DispatchResolvedCredential> resolved,
        string message)
    {
        DisposeAll(resolved);
        return RuntimeCredentialResolutionResult.Failed(message);
    }

    private static void DisposeAll(Dictionary<string, DispatchResolvedCredential> resolved)
    {
        foreach (var credential in resolved.Values)
        {
            credential.Dispose();
        }

        resolved.Clear();
    }

    private static string? NormalizeOptionalValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class UnavailableRuntimeCredentialPrompt : IRuntimeCredentialPrompt
{
    public Task<SecureString> PromptForPasswordAsync(
        RuntimeCredentialPromptRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Runtime credential prompting is not available in this host.");
}
