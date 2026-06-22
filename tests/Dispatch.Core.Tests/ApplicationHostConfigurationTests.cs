using Dispatch.Core.Configuration;
using Dispatch.Core.Credentials;
using Dispatch.Core.Execution;
using Dispatch.Core.Hosting;
using Dispatch.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dispatch.Core.Tests;

public sealed class ApplicationHostConfigurationTests
{
    [Fact]
    public void CoreServicesResolveWithDefaultOptions()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        var options = provider.GetRequiredService<IOptions<DispatchOptions>>().Value;

        Assert.Null(options.Inventory);
        Assert.Null(options.Target);
        Assert.Null(options.Exclude);
        Assert.Equal(@"C:\ProgramData\Dispatch\Runs", options.LocalRunRoot);
        Assert.Equal(@"C:\ProgramData\Dispatch\Runs", options.RemoteRunRoot);
        Assert.Equal(TransportKind.PsExec, options.DefaultTransport);
        Assert.Equal(8, options.Throttle);
        Assert.Equal([0], options.ExpectedExitCodes);
        Assert.Equal("psexec.exe", options.PsExecPath);
        Assert.Equal("none", options.CredentialProvider);
        Assert.Equal(@"C:\ProgramData\Dispatch\Credentials\references.json", options.CredentialStorePath);
        Assert.NotNull(provider.GetRequiredService<IDispatchPlanner>());
        Assert.NotNull(provider.GetRequiredService<IDispatchExecutor>());
        Assert.IsType<UnavailableCredentialProvider>(provider.GetRequiredService<ICredentialProvider>());
    }

    [Fact]
    public void CoreOptionsBindFromConfiguration()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Dispatch:Inventory"] = "hosts\\prod.yml",
            ["Dispatch:Target"] = "web",
            ["Dispatch:Exclude"] = "tag:canary",
            ["Dispatch:LocalRunRoot"] = "D:\\Dispatch\\Runs",
            ["Dispatch:RemoteRunRoot"] = "D:\\RemoteDispatch",
            ["Dispatch:DefaultTransport"] = "PsExec",
            ["Dispatch:Throttle"] = "16",
            ["Dispatch:PsExecPath"] = "C:\\Tools\\PsExec.exe",
            ["Dispatch:CredentialProvider"] = "file",
            ["Dispatch:CredentialStorePath"] = "D:\\Dispatch\\Credentials\\references.json",
            ["Dispatch:ExpectedExitCodes:0"] = "0",
            ["Dispatch:ExpectedExitCodes:1"] = "3010"
        });

        var options = provider.GetRequiredService<IOptions<DispatchOptions>>().Value;

        Assert.Equal("hosts\\prod.yml", options.Inventory);
        Assert.Equal("web", options.Target);
        Assert.Equal("tag:canary", options.Exclude);
        Assert.Equal("D:\\Dispatch\\Runs", options.LocalRunRoot);
        Assert.Equal("D:\\RemoteDispatch", options.RemoteRunRoot);
        Assert.Equal(TransportKind.PsExec, options.DefaultTransport);
        Assert.Equal(16, options.Throttle);
        Assert.Equal("C:\\Tools\\PsExec.exe", options.PsExecPath);
        Assert.Equal("file", options.CredentialProvider);
        Assert.Equal("D:\\Dispatch\\Credentials\\references.json", options.CredentialStorePath);
        Assert.Equal([0, 3010], options.ExpectedExitCodes);
        Assert.IsType<FileCredentialProvider>(provider.GetRequiredService<ICredentialProvider>());
    }

    [Fact]
    public async Task CoreServicesUseConfigCredentialCatalogWhenCredentialsAreDefined()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Dispatch:CredentialProvider"] = "prompt",
            ["Credentials:prod-admin:Provider"] = "prompt",
            ["Credentials:prod-admin:Username"] = @"CONTOSO\prod.admin"
        });

        var credentialProvider = Assert.IsType<ConfigurationCredentialProvider>(
            provider.GetRequiredService<ICredentialProvider>());

        var list = await credentialProvider.ListAsync(CancellationToken.None);
        var test = await credentialProvider.TestAsync(new CredentialReferenceRequest("prod-admin"), CancellationToken.None);
        var add = await credentialProvider.AddAsync(new CredentialAddRequest("prod-admin", null), CancellationToken.None);

        Assert.Equal("config", list.ProviderName);
        Assert.True(list.ProviderAvailable);
        Assert.True(list.Succeeded);
        var reference = Assert.Single(list.References);
        Assert.Equal("prod-admin", reference.Name);
        Assert.Equal(@"CONTOSO\prod.admin", reference.UserName);
        Assert.True(test.Succeeded);
        Assert.True(add.Succeeded);
        Assert.Contains("No enrollment required", add.Message);
    }

    [Theory]
    [InlineData("unsupported", @"CONTOSO\prod.admin", null, null, null, null, "unsupported provider")]
    [InlineData("prompt", null, null, null, null, null, "missing required field(s)")]
    [InlineData("pscredential", null, null, null, null, null, "missing required field(s)")]
    [InlineData("dpapi_file", @"CONTOSO\prod.admin", null, null, null, null, "path")]
    [InlineData("windows_credential_manager", @"CONTOSO\prod.admin", null, null, null, null, "target")]
    [InlineData("azure_keyvault", @"CONTOSO\prod.admin", null, null, "prod-admin-password", "default_azure_credential", "vault_uri")]
    [InlineData("azure_keyvault", @"CONTOSO\prod.admin", null, "https://scf-dispatch-kv.vault.azure.net/", "prod-admin-password", "bad_auth", "unsupported azure_keyvault auth")]
    public async Task ConfigCredentialCatalogValidatesProviderMetadata(
        string providerName,
        string? username,
        string? path,
        string? vaultUri,
        string? secretName,
        string? auth,
        string expectedMessage)
    {
        var values = new Dictionary<string, string?>
        {
            ["Dispatch:CredentialProvider"] = "prompt",
            ["Credentials:prod-admin:Provider"] = providerName
        };

        AddOptional(values, "Credentials:prod-admin:Username", username);
        AddOptional(values, "Credentials:prod-admin:Path", path);
        AddOptional(values, "Credentials:prod-admin:VaultUri", vaultUri);
        AddOptional(values, "Credentials:prod-admin:SecretName", secretName);
        AddOptional(values, "Credentials:prod-admin:Auth", auth);

        using var serviceProvider = BuildProvider(values);
        var credentialProvider = Assert.IsType<ConfigurationCredentialProvider>(
            serviceProvider.GetRequiredService<ICredentialProvider>());

        var result = await credentialProvider.TestAsync(new CredentialReferenceRequest("prod-admin"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(expectedMessage, result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("pscredential")]
    [InlineData("dpapi_file")]
    [InlineData("windows_credential_manager")]
    [InlineData("azure_keyvault")]
    public async Task ConfigCredentialCatalogAcceptsSupportedProviderMetadata(string providerName)
    {
        using var provider = BuildProvider(CreateValidCredentialValues(providerName));
        var credentialProvider = Assert.IsType<ConfigurationCredentialProvider>(
            provider.GetRequiredService<ICredentialProvider>());

        var result = await credentialProvider.TestAsync(new CredentialReferenceRequest("prod-admin"), CancellationToken.None);

        if (providerName == "dpapi_file")
        {
            Assert.False(result.Succeeded);
            Assert.Contains("DPAPI credential file", result.Message);
            Assert.DoesNotContain("missing required field", result.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }

        if (providerName == "windows_credential_manager")
        {
            Assert.False(result.Succeeded);
            Assert.Contains("Windows Credential Manager target", result.Message);
            Assert.DoesNotContain("missing required field", result.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }

        Assert.True(result.Succeeded);
        Assert.Contains(providerName, result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlannerAndExecutorAreRegistered()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        using var script = TemporaryScript.Create();

        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);

        Assert.NotEmpty(plan.RunId);
        Assert.Single(plan.Targets);

        var result = await executor.ExecuteAsync(plan, CancellationToken.None);
        var target = Assert.Single(result.Targets);

        Assert.Equal(TargetExecutionState.Failed, target.State);
        Assert.Equal(FailureCategory.TransportUnavailable, target.FailureCategory);
    }

    private static ServiceProvider BuildProvider(IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddDispatchCore(configuration)
            .BuildServiceProvider(validateScopes: true);
    }

    private static void AddOptional(IDictionary<string, string?> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[key] = value;
        }
    }

    private static Dictionary<string, string?> CreateValidCredentialValues(string providerName)
    {
        var values = new Dictionary<string, string?>
        {
            ["Dispatch:CredentialProvider"] = "prompt",
            ["Credentials:prod-admin:Provider"] = providerName,
            ["Credentials:prod-admin:Username"] = @"CONTOSO\prod.admin"
        };

        switch (providerName)
        {
            case "dpapi_file":
                values["Credentials:prod-admin:Path"] = @"C:\ProgramData\Dispatch\Credentials\prod-admin.cred";
                break;
            case "windows_credential_manager":
                values["Credentials:prod-admin:Target"] = "Dispatch/prod-admin";
                break;
            case "azure_keyvault":
                values["Credentials:prod-admin:VaultUri"] = "https://scf-dispatch-kv.vault.azure.net/";
                values["Credentials:prod-admin:SecretName"] = "prod-admin-password";
                values["Credentials:prod-admin:Auth"] = "default_azure_credential";
                break;
        }

        return values;
    }
}
