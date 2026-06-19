using Dispatch.Core.Configuration;
using Dispatch.Core.Credentials;
using Dispatch.Core.Models;
using Dispatch.Core.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Runtime.Versioning;

namespace Dispatch.Cli.Tests;

[SupportedOSPlatform("windows")]
public sealed class DispatchCliHostTests
{
    [Fact]
    public void HostLoadsDefaultGlobalYamlConfigWhenPresent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dispatch-global-config-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, "config.yml");
        var storePath = Path.Combine(root, "credentials", "references.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(configPath, $"""
            dispatch:
              default_transport: psrp
              default_credential_provider: file
              credential_store_path: {storePath}
            """);

        try
        {
            using var host = DispatchCliHost.Build([], configPath);

            var options = host.Services.GetRequiredService<IOptions<DispatchOptions>>().Value;

            Assert.Equal(TransportKind.Psrp, options.DefaultTransport);
            Assert.Equal("file", options.CredentialProvider);
            Assert.Equal(storePath, options.CredentialStorePath);
            Assert.IsType<FileCredentialProvider>(host.Services.GetRequiredService<ICredentialProvider>());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HostRegistersWinRmAndPsrpTransportServices()
    {
        var missingGlobalConfigPath = Path.Combine(
            Path.GetTempPath(),
            $"dispatch-missing-global-config-{Guid.NewGuid():N}",
            "config.yml");
        using var host = DispatchCliHost.Build([], missingGlobalConfigPath);

        var executorKinds = host.Services
            .GetServices<ITransportScriptExecutor>()
            .Select(static executor => executor.Kind)
            .ToArray();
        var probeKinds = host.Services
            .GetServices<ITransportEndpointProbe>()
            .Select(static probe => probe.Kind)
            .ToArray();
        var descriptorKinds = host.Services
            .GetServices<ITransportDescriptor>()
            .Select(static descriptor => descriptor.Kind)
            .ToArray();

        Assert.Contains(TransportKind.WinRm, executorKinds);
        Assert.Contains(TransportKind.WinRm, probeKinds);
        Assert.Contains(TransportKind.WinRm, descriptorKinds);
        Assert.Contains(TransportKind.Psrp, executorKinds);
        Assert.Contains(TransportKind.Psrp, probeKinds);
        Assert.Contains(TransportKind.Psrp, descriptorKinds);
    }
}
