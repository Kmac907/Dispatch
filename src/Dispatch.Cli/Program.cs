using Dispatch.Cli;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace Dispatch.Cli;

[SupportedOSPlatform("windows")]
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var host = DispatchCliHost.Build(args);
        var application = host.Services.GetRequiredService<DispatchCliApplication>();
        return await application.RunAsync(args, CancellationToken.None).ConfigureAwait(false);
    }
}
