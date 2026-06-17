using Dispatch.Core.Transports;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Transports.WinRm;

public static class WinRmServiceCollectionExtensions
{
    public static IServiceCollection AddDispatchWinRmTransport(this IServiceCollection services)
    {
        services.AddSingleton<ITransportDescriptor, WinRmTransportDescriptor>();
        services.AddSingleton<IWinRmDnsResolver, WinRmDnsResolver>();
        services.AddSingleton<IWinRmPortProbe, WinRmPortProbe>();
        services.AddSingleton<ITransportEndpointProbe, WinRmEndpointProbe>();
        services.AddSingleton<ITransportScriptExecutor, WinRmScriptExecutor>();
        return services;
    }
}
