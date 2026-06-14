using Dispatch.Core;
using Dispatch.Core.Configuration;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Dispatch.Transports.PsExec;
using Microsoft.Extensions.Options;

namespace Dispatch.Cli;

public sealed class DispatchCliApplication(
    IOptions<DispatchOptions> options,
    IDispatchPlanner planner,
    IDispatchExecutor executor)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        _ = executor;

        if (args.Any(static arg => arg is "--version" or "-v"))
        {
            Console.WriteLine(DispatchProduct.Version);
            return 0;
        }

        if (args.Length == 0 || args.Any(static arg => arg is "--help" or "-h" or "/?"))
        {
            Console.WriteLine($"""
Dispatch {DispatchProduct.Version}

Windows-native script orchestration for endpoint administrators.

Usage:
  dispatch [--help]
  dispatch --version
  dispatch run --dry-run --script <path> --computer-name <name> [options] [-- <script-args>]

Run options:
  --transport <psexec|psrp|winrm>
  --expected-exit-code <code[,code]>
  --throttle <count>
  --run-as-system
  --output-root <path>
  --remote-root <path>

Available transports:
  {PsExecTransportDescriptor.TransportName}

Only dry-run planning is implemented in this slice.
""");
            return 0;
        }

        if (args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            return await RunDryRunAsync(args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false);
        }

        Console.Error.WriteLine("Unknown arguments. Run 'dispatch --help' for usage.");
        return 1;
    }

    private async Task<int> RunDryRunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!DispatchRunCommandParser.TryParse(
                args,
                options.Value.DefaultTransport,
                options.Value.ExpectedExitCodes,
                out var command,
                out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        try
        {
            var plan = await planner.CreatePlanAsync(command!.ToRequest(), cancellationToken).ConfigureAwait(false);
            Console.WriteLine(DispatchJson.Serialize(plan));
            return 0;
        }
        catch (DispatchPlanningException exception)
        {
            foreach (var validationError in exception.Errors)
            {
                Console.Error.WriteLine($"{validationError.Code}: {validationError.Message}");
            }

            return 1;
        }
    }
}
