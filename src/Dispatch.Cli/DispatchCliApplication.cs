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
    private readonly DispatchRunHistoryReader runHistoryReader = new();

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
                new DispatchRunCommandParser.DispatchRunAmbientConfig(
                    options.Value.Inventory,
                    options.Value.Target,
                    options.Value.Exclude,
                    options.Value.DefaultTransport),
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
                if (command.OutputMode == DispatchOutputMode.Ndjson)
                {
                    var streamWriter = new DispatchNdjsonStreamWriter(Console.Out, command.Verbose, command.Trace);
                    streamWriter.WritePlanningStarted();
                    var ndjsonPlan = await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
                    streamWriter.WritePlan(ndjsonPlan);
                    return 0;
                }

                var dryRunPlan = command.OutputMode == DispatchOutputMode.Rich && !command.Quiet
                    ? await CreatePlanWithDryRunProgressAsync(request, command.NoColor, cancellationToken).ConfigureAwait(false)
                    : await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
                if (!ShouldSuppressOutput(command))
                {
                    DispatchStructuredOutputRenderer.RenderPlan(Console.Out, dryRunPlan, command.OutputMode);
                }

                return 0;
            }

            if (command.OutputMode == DispatchOutputMode.Ndjson)
            {
                var streamWriter = new DispatchNdjsonStreamWriter(Console.Out, command.Verbose, command.Trace);
                streamWriter.WritePlanningStarted();
                var streamPlan = await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
                streamWriter.WritePlan(streamPlan);
                streamWriter.WriteExecutionStarted(streamPlan);
                var streamResult = await executor.ExecuteAsync(streamPlan, streamWriter, cancellationToken).ConfigureAwait(false);
                streamWriter.WriteResult(streamResult);
                return streamResult.FailedCount == 0 && streamResult.TimedOutCount == 0 && streamResult.CancelledCount == 0 ? 0 : 1;
            }

            var plan = command.OutputMode == DispatchOutputMode.Rich && !command.Quiet
                ? await CreatePlanWithStatusAsync(request, command.NoColor, cancellationToken).ConfigureAwait(false)
                : await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
            var result = command.OutputMode == DispatchOutputMode.Rich && !command.Quiet
                ? await RunWithSpectreProgressAsync(plan, command.NoDashboard, command.NoColor, cancellationToken).ConfigureAwait(false)
                : await executor.ExecuteAsync(plan, NullDispatchExecutionObserver.Instance, cancellationToken).ConfigureAwait(false);
            if (!ShouldSuppressOutput(command))
            {
                DispatchStructuredOutputRenderer.RenderRunResult(Console.Out, result, command.OutputMode);
            }

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
        bool noColor,
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
                useLiveDisplay,
                noColor);
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
                useLiveDisplay: false,
                noColor);
            return await renderer.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ExecutionPlan> CreatePlanWithStatusAsync(
        DispatchRequest request,
        bool noColor,
        CancellationToken cancellationToken) =>
        await SpectreConsoleRenderer.RunPlanningStatusAsync(
                Console.Out,
                token => planner.CreatePlanAsync(request, token),
                noColor,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<ExecutionPlan> CreatePlanWithDryRunProgressAsync(
        DispatchRequest request,
        bool noColor,
        CancellationToken cancellationToken) =>
        await SpectreConsoleRenderer.RunDryRunPlanningProgressAsync(
                Console.Out,
                token => planner.CreatePlanAsync(request, token),
                noColor,
                cancellationToken)
            .ConfigureAwait(false);

    private static bool ShouldSuppressOutput(DispatchRunCommand command) =>
        command.Quiet && command.OutputMode is DispatchOutputMode.Rich or DispatchOutputMode.Table;

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

    private static bool IsSpectreRegisteredCommand(string command) =>
        command is "apply" or "run" or "push" or "hosts" or "logs" or "creds" or "doctor" or "init" or "version";

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

    internal int RunLogsListCommand(string? outputValue)
    {
        if (!TryParseOutputMode(outputValue, out var outputMode, out var error))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", error!);
            return 1;
        }

        var runs = runHistoryReader.ListRuns(options.Value.LocalRunRoot);
        if (runs.Count == 0)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Logs Not Found",
                $"No completed runs were found under '{options.Value.LocalRunRoot}'.");
            return 1;
        }

        DispatchStructuredOutputRenderer.RenderRunHistory(Console.Out, options.Value.LocalRunRoot, runs, outputMode);
        return 0;
    }

    internal int RunLogsShowCommand(string? selector, string? outputValue)
    {
        if (!TryParseOutputMode(outputValue, out var outputMode, out var error))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", error!);
            return 1;
        }

        var result = runHistoryReader.ReadRun(options.Value.LocalRunRoot, selector);
        if (result is null)
        {
            var missingSelector = string.IsNullOrWhiteSpace(selector) ? "latest" : selector;
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Logs Not Found",
                $"Run '{missingSelector}' was not found under '{options.Value.LocalRunRoot}'.");
            return 1;
        }

        DispatchStructuredOutputRenderer.RenderRunResult(Console.Out, result, outputMode);
        return 0;
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

    private static bool TryParseOutputMode(string? value, out DispatchOutputMode outputMode, out string? error)
    {
        outputMode = DispatchOutputMode.Rich;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        outputMode = value.Trim().ToLowerInvariant() switch
        {
            "rich" => DispatchOutputMode.Rich,
            "table" => DispatchOutputMode.Table,
            "json" => DispatchOutputMode.Json,
            "ndjson" => DispatchOutputMode.Ndjson,
            "yaml" => DispatchOutputMode.Yaml,
            _ => default
        };

        if (value.Trim().Equals("rich", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("table", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("json", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("ndjson", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("yaml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        error = $"Unsupported output mode '{value}'. Expected rich, table, json, ndjson, or yaml.";
        return false;
    }

}
