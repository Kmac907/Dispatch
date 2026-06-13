using Dispatch.Core;
using Dispatch.Core.Configuration;
using Dispatch.Core.Execution;
using Dispatch.Transports.PsExec;
using Microsoft.Extensions.Options;

namespace Dispatch.Cli;

public sealed class DispatchCliApplication(
    IOptions<DispatchOptions> options,
    IDispatchPlanner planner,
    IDispatchExecutor executor)
{
    public Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        _ = planner;
        _ = executor;
        _ = options.Value;

        if (args.Any(static arg => arg is "--version" or "-v"))
        {
            Console.WriteLine(DispatchProduct.Version);
            return Task.FromResult(0);
        }

        if (args.Length == 0 || args.Any(static arg => arg is "--help" or "-h" or "/?"))
        {
            Console.WriteLine($"""
Dispatch {DispatchProduct.Version}

Windows-native script orchestration for endpoint administrators.

Usage:
  dispatch [--help]
  dispatch --version

Available transports:
  {PsExecTransportDescriptor.TransportName}

Remote execution commands are not implemented in this foundation slice.
""");
            return Task.FromResult(0);
        }

        Console.Error.WriteLine("Unknown arguments. Run 'dispatch --help' for usage.");
        return Task.FromResult(1);
    }
}
