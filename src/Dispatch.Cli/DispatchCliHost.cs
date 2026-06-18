using Dispatch.Core.Hosting;
using Dispatch.Core.Credentials;
using Dispatch.Transports.PsExec;
using Dispatch.Transports.Psrp;
using Dispatch.Transports.WinRm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;

namespace Dispatch.Cli;

[SupportedOSPlatform("windows")]
public static class DispatchCliHost
{
    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.local.json", optional: true)
            .AddEnvironmentVariables(prefix: "DISPATCH_");

        builder.Logging.AddConsole();
        builder.Services.AddDispatchCore(builder.Configuration);
        builder.Services.AddDispatchPsExecTransport();
        builder.Services.AddDispatchPsrpTransport();
        builder.Services.AddDispatchWinRmTransport();
        builder.Services.AddSingleton<IDispatchDoctor, DispatchDoctor>();
        builder.Services.AddSingleton(static services => new DispatchCliApplication(
            services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Dispatch.Core.Configuration.DispatchOptions>>(),
            services.GetRequiredService<Dispatch.Core.Execution.IDispatchPlanner>(),
            services.GetRequiredService<Dispatch.Core.Execution.IDispatchExecutor>(),
            services.GetRequiredService<IDispatchDoctor>(),
            credentialProvider: services.GetRequiredService<ICredentialProvider>()));

        return builder.Build();
    }
}
