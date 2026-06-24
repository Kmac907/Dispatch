using Dispatch.Core.Hosting;
using Dispatch.Core.Credentials;
using Dispatch.Core.Defaults;
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
    public static IHost Build(string[] args) => Build(args, DispatchDefaults.GlobalConfigPath);

    internal static IHost Build(string[] args, string globalConfigPath)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.local.json", optional: true);

        if (File.Exists(globalConfigPath))
        {
            builder.Configuration.AddInMemoryCollection(DispatchConfigFileReader.ReadYamlFile(globalConfigPath));
        }

        builder.Configuration.AddEnvironmentVariables(prefix: "DISPATCH_");

        builder.Logging.AddConsole();
        builder.Services.AddDispatchCore(builder.Configuration);
        builder.Services.AddSingleton<IRuntimeCredentialPrompt, ConsoleRuntimeCredentialPrompt>();
        builder.Services.AddDispatchPsExecTransport();
        builder.Services.AddDispatchPsrpTransport();
        builder.Services.AddDispatchWinRmTransport();
        builder.Services.AddSingleton<IDispatchDoctor, DispatchDoctor>();
        builder.Services.AddSingleton(static services => new DispatchCliApplication(
            services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Dispatch.Core.Configuration.DispatchOptions>>(),
            services.GetRequiredService<Dispatch.Core.Execution.IDispatchPlanner>(),
            services.GetRequiredService<Dispatch.Core.Execution.IDispatchExecutor>(),
            services.GetRequiredService<IDispatchDoctor>(),
            credentialProvider: services.GetRequiredService<ICredentialProvider>(),
            runtimeCredentialResolver: services.GetRequiredService<IRuntimeCredentialResolver>(),
            runtimeCredentialPrompt: services.GetRequiredService<IRuntimeCredentialPrompt>(),
            winRmTransferClient: services.GetRequiredService<IWinRmScriptTransferClient>(),
            psrpFileTransferClient: services.GetRequiredService<IPsrpFileTransferClient>()));

        return builder.Build();
    }
}
