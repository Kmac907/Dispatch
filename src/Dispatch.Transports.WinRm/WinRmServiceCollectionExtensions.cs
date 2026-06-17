using Dispatch.Core.Execution;
using Dispatch.Core.Transports;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Dispatch.Transports.WinRm;

[SupportedOSPlatform("windows")]
public static class WinRmServiceCollectionExtensions
{
    public static IServiceCollection AddDispatchWinRmTransport(this IServiceCollection services)
    {
        services.AddSingleton<ITransportDescriptor, WinRmTransportDescriptor>();
        services.AddSingleton<IWinRmDnsResolver, WinRmDnsResolver>();
        services.AddSingleton<IWinRmPortProbe, WinRmPortProbe>();
        services.AddSingleton<IWinRmShellClient, WinRmShellClient>();
        services.AddSingleton<IWinRmScriptTransferClient, WinRmScriptTransferClient>();
        services.AddSingleton<ITransportArtifactCollector, WinRmArtifactCollector>();
        services.AddSingleton<ITransportEndpointProbe, WinRmEndpointProbe>();
        services.AddSingleton<ITransportScriptExecutor, WinRmScriptExecutor>();
        return services;
    }
}
