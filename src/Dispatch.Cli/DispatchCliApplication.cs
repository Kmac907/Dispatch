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
    IDispatchExecutor executor,
    IDispatchDoctor doctor,
    DispatchRunDisplayMode displayMode = DispatchRunDisplayMode.Auto,
    TextWriter? statusWriter = null)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Any(static arg => arg is "--version" or "-v"))
        {
            SpectreConsoleRenderer.RenderVersion(Console.Out);
            return 0;
        }

        if (args.Length == 0)
        {
            return RenderRootHelp();
        }

        if (IsRootHelpRequest(args))
        {
            return RenderRootHelp();
        }

        var commandName = args[0].ToLowerInvariant();
        if (commandName == "run" && IsExplicitHelpRequest(args.Skip(1).ToArray()))
        {
            return RenderRunHelp();
        }

        if (commandName == "doctor" && IsExplicitHelpRequest(args.Skip(1).ToArray()))
        {
            return RenderDoctorHelp();
        }

        if (commandName == "run" && args.Length == 1)
        {
            return RenderRunHelp();
        }

        if (commandName == "run" && IsLegacyRunCompatibilityRequest(args))
        {
            return await RunCommandAsync(args[1..], cancellationToken).ConfigureAwait(false);
        }

        if (IsPlannedTopLevelCommand(commandName))
        {
            return commandName switch
            {
                "apply" => RenderPlannedCommand("apply", "6.5 YAML Apply And Job Model"),
                "push" => RenderPlannedCommand("push", "6.6 Push, Hosts, Doctor, And Init Command Surfaces"),
                "hosts" => RenderPlannedCommand("hosts", "6.6 Push, Hosts, Doctor, And Init Command Surfaces"),
                "logs" => RenderPlannedCommand("logs", "6.3 Structured Run Logs And Log Commands"),
                "creds" => RenderPlannedCommand("creds", "6.4 Credential References"),
                "init" => RenderPlannedCommand("init", "6.6 Push, Hosts, Doctor, And Init Command Surfaces"),
                _ => RenderUnknownCommand(commandName)
            };
        }

        if (IsSpectreRegisteredCommand(commandName))
        {
            return await new DispatchSpectreCommandApp(this).RunAsync(args, cancellationToken).ConfigureAwait(false);
        }

        return RenderUnknownCommand(args[0]);

    }

    internal async Task<int> RunCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!DispatchRunCommandParser.TryParse(
                args,
                options.Value.DefaultTransport,
                options.Value.ExpectedExitCodes,
                out var command,
                out var error))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", error);
            return 1;
        }

        try
        {
            var request = command!.ToRequest();
            if (command.DryRun)
            {
                var dryRunPlan = await CreatePlanWithDryRunProgressAsync(request, cancellationToken).ConfigureAwait(false);
                SpectreConsoleRenderer.RenderDryRunPlan(Console.Out, dryRunPlan);
                return 0;
            }

            var plan = await CreatePlanWithStatusAsync(request, cancellationToken).ConfigureAwait(false);
            var result = await RunWithSpectreProgressAsync(plan, command.NoDashboard, cancellationToken).ConfigureAwait(false);
            SpectreConsoleRenderer.RenderRunResult(Console.Out, result);
            return result.FailedCount == 0 && result.TimedOutCount == 0 && result.CancelledCount == 0 ? 0 : 1;
        }
        catch (DispatchPlanningException exception)
        {
            var message = string.Join(
                Environment.NewLine,
                exception.Errors.Select(static validationError => $"{validationError.Code}: {validationError.Message}"));
            SpectreConsoleRenderer.RenderError(Console.Error, "Dispatch Planning Failed", message);

            return 1;
        }
    }

    private async Task<DispatchRunResult> RunWithSpectreProgressAsync(
        ExecutionPlan plan,
        bool noDashboard,
        CancellationToken cancellationToken)
    {
        var useLiveDisplay = ShouldUseLiveDashboard(noDashboard);
        if (!useLiveDisplay && Console.IsErrorRedirected && statusWriter is null)
        {
            return await executor.ExecuteAsync(plan, NullDispatchExecutionObserver.Instance, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            var renderer = new SpectreLiveRunRenderer(
                plan,
                executor,
                statusWriter ?? Console.Error,
                useLiveDisplay);
            return await renderer.ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException exception) when (displayMode == DispatchRunDisplayMode.Auto && useLiveDisplay)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Live Dashboard Unavailable",
                $"Dispatch is using append-only Spectre progress for this run. {exception.Message}");
            var renderer = new SpectreLiveRunRenderer(
                plan,
                executor,
                statusWriter ?? Console.Error,
                useLiveDisplay: false);
            return await renderer.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ExecutionPlan> CreatePlanWithStatusAsync(
        DispatchRequest request,
        CancellationToken cancellationToken) =>
        await SpectreConsoleRenderer.RunPlanningStatusAsync(
                Console.Out,
                token => planner.CreatePlanAsync(request, token),
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<ExecutionPlan> CreatePlanWithDryRunProgressAsync(
        DispatchRequest request,
        CancellationToken cancellationToken) =>
        await SpectreConsoleRenderer.RunDryRunPlanningProgressAsync(
                Console.Out,
                token => planner.CreatePlanAsync(request, token),
                cancellationToken)
            .ConfigureAwait(false);

    private bool ShouldUseLiveDashboard(bool noDashboard) =>
        displayMode switch
        {
            DispatchRunDisplayMode.LiveDashboard => true,
            DispatchRunDisplayMode.AppendOnly => false,
            _ => !noDashboard && !Console.IsErrorRedirected
        };

    private static bool IsRootHelpRequest(IReadOnlyList<string> args) =>
        args.Count == 1 && args[0] is "--help" or "-h" or "-?";

    private static bool IsExplicitHelpRequest(IReadOnlyList<string> args) =>
        args.Any(static arg => arg is "--help" or "-h" or "-?");

    private static bool IsPlannedTopLevelCommand(string command) =>
        command is "apply" or "push" or "hosts" or "logs" or "creds" or "init";

    private static bool IsSpectreRegisteredCommand(string command) =>
        command is "run" or "doctor" or "version";

    private static bool IsLegacyRunCompatibilityRequest(IReadOnlyList<string> args) =>
        args.Count > 1 && !args[1].Equals("ps", StringComparison.OrdinalIgnoreCase)
                       && !args[1].Equals("cmd", StringComparison.OrdinalIgnoreCase)
                       && !args[1].Equals("exe", StringComparison.OrdinalIgnoreCase);

    private static int RenderRootHelp()
    {
        SpectreConsoleRenderer.RenderRootHelp(Console.Out);
        return 0;
    }

    internal static int RenderVersion()
    {
        SpectreConsoleRenderer.RenderVersion(Console.Out);
        return 0;
    }

    private static int RenderRunHelp()
    {
        SpectreConsoleRenderer.RenderRunHelp(Console.Out);
        return 0;
    }

    private static int RenderDoctorHelp()
    {
        SpectreConsoleRenderer.RenderDoctorHelp(Console.Out);
        return 0;
    }

    internal int RunDoctorCommand()
    {
        var report = doctor.Run();
        SpectreConsoleRenderer.RenderDoctorReport(Console.Out, report);
        return report.Succeeded ? 0 : 1;
    }

    internal static int RenderPlannedCommand(string command, string roadmapItem)
    {
        SpectreConsoleRenderer.RenderPlannedFeature(Console.Error, command, roadmapItem);
        return 1;
    }

    private static int RenderUnknownCommand(string command)
    {
        SpectreConsoleRenderer.RenderError(
            Console.Error,
            "Unknown Dispatch Command",
            $"'{command}' is not a Dispatch command.");
        return 1;
    }

}
