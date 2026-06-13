using Dispatch.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dispatch.Cli;

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
        builder.Services.AddSingleton<DispatchCliApplication>();

        return builder.Build();
    }
}
