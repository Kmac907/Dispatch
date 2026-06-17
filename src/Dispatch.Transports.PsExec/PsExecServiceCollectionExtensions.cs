using Dispatch.Core.Transports;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Transports.PsExec;

public static class PsExecServiceCollectionExtensions
{
    public static IServiceCollection AddDispatchPsExecTransport(this IServiceCollection services)
    {
        services.AddSingleton<ITransportDescriptor, PsExecTransportDescriptor>();
        services.AddSingleton<PsExecCommandBuilder>();
        services.AddSingleton<IPsExecDnsResolver, PsExecDnsResolver>();
        services.AddSingleton<IPsExecPortProbe, PsExecPortProbe>();
        services.AddSingleton<IPsExecAdminShareProbe, PsExecAdminShareProbe>();
        services.AddSingleton<ITransportEndpointProbe, PsExecEndpointProbe>();
        services.AddSingleton<IPsExecProcessRunner, PsExecProcessRunner>();
        services.AddSingleton<ITransportScriptExecutor, PsExecScriptExecutor>();
        return services;
    }
}
