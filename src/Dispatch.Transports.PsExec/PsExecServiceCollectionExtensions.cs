using Dispatch.Core.Transports;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Transports.PsExec;

public static class PsExecServiceCollectionExtensions
{
    public static IServiceCollection AddDispatchPsExecTransport(this IServiceCollection services)
    {
        services.AddSingleton<PsExecCommandBuilder>();
        services.AddSingleton<IPsExecProcessRunner, PsExecProcessRunner>();
        services.AddSingleton<ITransportScriptExecutor, PsExecScriptExecutor>();
        return services;
    }
}
