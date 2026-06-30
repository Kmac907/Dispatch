using Dispatch.Core.Hosting;
using Dispatch.Core.Credentials;
using Dispatch.Core.Defaults;
using Dispatch.Core.Transports;
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
            if (IsDoctorCommand(args))
            {
                TryAddGlobalConfigForDoctor(builder.Configuration, globalConfigPath);
            }
            else
            {
                builder.Configuration.AddInMemoryCollection(DispatchConfigFileReader.ReadYamlFile(globalConfigPath));
            }
        }

        builder.Configuration.AddEnvironmentVariables(prefix: "DISPATCH_");

        builder.Logging.AddConsole();
        builder.Services.AddDispatchCore(builder.Configuration);
        builder.Services.AddSingleton<IRuntimeCredentialPrompt, ConsoleRuntimeCredentialPrompt>();
        builder.Services.AddDispatchPsExecTransport();
        builder.Services.AddDispatchPsrpTransport();
        builder.Services.AddDispatchWinRmTransport();
        builder.Services.AddSingleton<IDispatchDoctor>(services => new DispatchDoctor(
            services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Dispatch.Core.Configuration.DispatchOptions>>(),
            services.GetRequiredService<ICredentialProvider>(),
            RegistryPsExecEulaStateReader.Instance,
            globalConfigPath));
        builder.Services.AddSingleton(static services => new DispatchCliApplication(
            services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Dispatch.Core.Configuration.DispatchOptions>>(),
            services.GetRequiredService<Dispatch.Core.Execution.IDispatchPlanner>(),
            services.GetRequiredService<Dispatch.Core.Execution.IDispatchExecutor>(),
            services.GetRequiredService<IDispatchDoctor>(),
            credentialProvider: services.GetRequiredService<ICredentialProvider>(),
            runtimeCredentialResolver: services.GetRequiredService<IRuntimeCredentialResolver>(),
            runtimeCredentialPrompt: services.GetRequiredService<IRuntimeCredentialPrompt>(),
            winRmTransferClient: services.GetRequiredService<IWinRmScriptTransferClient>(),
            psrpFileTransferClient: services.GetRequiredService<IPsrpFileTransferClient>(),
            winRmShellClient: services.GetRequiredService<IWinRmShellClient>(),
            psrpCommandClient: services.GetRequiredService<IPsrpCommandClient>(),
            endpointProbes: services.GetServices<ITransportEndpointProbe>()));

        return builder.Build();
    }

    private static bool IsDoctorCommand(string[] args) =>
        args.FirstOrDefault(static arg => !arg.StartsWith("-", StringComparison.Ordinal)) is { } command
        && command.Equals("doctor", StringComparison.OrdinalIgnoreCase);

    private static void TryAddGlobalConfigForDoctor(IConfigurationBuilder configuration, string globalConfigPath)
    {
        try
        {
            configuration.AddInMemoryCollection(DispatchConfigFileReader.ReadYamlFile(globalConfigPath));
        }
        catch (Exception exception) when (exception is ArgumentException
            or FormatException
            or InvalidDataException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            // The doctor command reports config parse failures through its normal diagnostics.
        }
    }
}
