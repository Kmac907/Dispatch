using Dispatch.Core.Models;
using Dispatch.Core.Transports;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli.Tests;

public sealed class DispatchCliHostTests
{
    [Fact]
    public void HostRegistersWinRmTransportServices()
    {
        using var host = DispatchCliHost.Build([]);

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
    }
}
