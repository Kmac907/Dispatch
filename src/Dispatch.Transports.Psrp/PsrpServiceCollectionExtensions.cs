using Dispatch.Core.Transports;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Dispatch.Transports.Psrp;

[SupportedOSPlatform("windows")]
public static class PsrpServiceCollectionExtensions
{
    public static IServiceCollection AddDispatchPsrpTransport(this IServiceCollection services)
    {
        services.AddSingleton<ITransportDescriptor, PsrpTransportDescriptor>();
        services.AddSingleton<IPsrpDnsResolver, PsrpDnsResolver>();
        services.AddSingleton<IPsrpPortProbe, PsrpPortProbe>();
        services.AddSingleton<ITransportEndpointProbe, PsrpEndpointProbe>();
        services.AddSingleton<ITransportScriptExecutor, PsrpScriptExecutor>();
        return services;
    }
}
