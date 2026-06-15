using Dispatch.Core;
using Dispatch.Core.Configuration;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Dispatch.Transports.PsExec;
using Microsoft.Extensions.Options;
using Terminal.Gui;

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
            TerminalGuiConsoleRenderer.RenderVersion(Console.Out);
            return 0;
        }

        if (args.Length == 0)
        {
            return Console.IsInputRedirected
                ? RenderRootHelp()
                : await RunInteractiveAsync(cancellationToken).ConfigureAwait(false);
        }

        if (IsRootHelpRequest(args))
        {
            return RenderRootHelp();
        }

        return args[0].ToLowerInvariant() switch
        {
            "run" => IsExplicitHelpRequest(args.Skip(1).ToArray())
                ? RenderRunHelp()
                : await RunCommandAsync(args[1..], cancellationToken).ConfigureAwait(false),
            "doctor" => IsExplicitHelpRequest(args.Skip(1).ToArray())
                ? RenderDoctorHelp()
                : RunDoctorCommand(),
            _ => RenderUnknownCommand(args[0])
        };
    }

    private async Task<int> RunCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!DispatchRunCommandParser.TryParse(
                args,
                options.Value.DefaultTransport,
                options.Value.ExpectedExitCodes,
                out var command,
                out var error))
        {
            TerminalGuiConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", error);
            return 1;
        }

        try
        {
            var request = command!.ToRequest();
            if (command.DryRun)
            {
                var dryRunPlan = await CreatePlanWithDryRunProgressAsync(request, cancellationToken).ConfigureAwait(false);
                TerminalGuiConsoleRenderer.RenderDryRunPlan(Console.Out, dryRunPlan);
                return 0;
            }

            var plan = await CreatePlanWithStatusAsync(request, cancellationToken).ConfigureAwait(false);
            var result = ShouldUseLiveDashboard(command.NoDashboard)
                ? await RunWithLiveDashboardAsync(plan, cancellationToken).ConfigureAwait(false)
                : await RunWithCompactProgressAsync(plan, cancellationToken).ConfigureAwait(false);
            TerminalGuiConsoleRenderer.RenderRunResult(Console.Out, result);
            return result.FailedCount == 0 && result.TimedOutCount == 0 && result.CancelledCount == 0 ? 0 : 1;
        }
        catch (DispatchPlanningException exception)
        {
            var message = string.Join(
                Environment.NewLine,
                exception.Errors.Select(static validationError => $"{validationError.Code}: {validationError.Message}"));
            TerminalGuiConsoleRenderer.RenderError(Console.Error, "Dispatch Planning Failed", message);

            return 1;
        }
    }

    private async Task<int> RunInteractiveAsync(CancellationToken cancellationToken)
    {
        var commandCenter = new TerminalGuiDispatchCommandCenter(doctor);
        var result = await commandCenter.RunAsync(cancellationToken).ConfigureAwait(false);

        return result.Kind switch
        {
            CommandCenterExitKind.StartRun => await RunCommandAsync(result.RunArguments, cancellationToken)
                .ConfigureAwait(false),
            _ => 0
        };
    }

    private async Task<DispatchRunResult> RunWithLiveDashboardAsync(
        ExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        var dashboard = new TerminalGuiDispatchRunDashboard(plan, DateTimeOffset.UtcNow);
        var writer = statusWriter ?? Console.Error;

        try
        {
            if (Console.IsErrorRedirected)
            {
                var redirectedResult = await executor.ExecuteAsync(
                        plan,
                        new TerminalGuiDispatchExecutionObserver(dashboard, static () => { }),
                        cancellationToken)
                    .ConfigureAwait(false);
                dashboard.Complete(redirectedResult);
                writer.Write(dashboard.RenderSnapshot());
                return redirectedResult;
            }

            Application.Init();
            try
            {
                var top = Application.Top;
                top.RemoveAll();
                var root = dashboard.BuildView();
                top.Add(root);
                Application.Refresh();

                void Refresh()
                {
                    root.RemoveAll();
                    root.Add(dashboard.BuildView().Subviews.ToArray());
                    Application.Refresh();
                }

                using var refreshTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var refreshTask = RefreshDashboardPeriodicallyAsync(Refresh, refreshTimer, refreshCancellation.Token);
                var observer = new TerminalGuiDispatchExecutionObserver(dashboard, Refresh);

                try
                {
                    var result = await executor.ExecuteAsync(plan, observer, cancellationToken).ConfigureAwait(false);
                    dashboard.Complete(result);
                    Refresh();
                    return result;
                }
                finally
                {
                    await refreshCancellation.CancelAsync().ConfigureAwait(false);
                    await refreshTask.ConfigureAwait(false);
                    writer.Write(dashboard.RenderSnapshot());
                }
            }
            finally
            {
                Application.Shutdown();
            }
        }
        catch (IOException exception) when (displayMode == DispatchRunDisplayMode.Auto)
        {
            TerminalGuiConsoleRenderer.RenderError(
                Console.Error,
                "Live Dashboard Unavailable",
                $"Dispatch is using compact Terminal.Gui progress for this run. {exception.Message}");
            return await RunWithCompactProgressAsync(plan, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DispatchRunResult> RunWithCompactProgressAsync(
        ExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        if (Console.IsErrorRedirected && statusWriter is null)
        {
            return await executor.ExecuteAsync(plan, NullDispatchExecutionObserver.Instance, cancellationToken)
                .ConfigureAwait(false);
        }

        var progress = new TerminalGuiCompactDispatchProgress(plan, executor, statusWriter ?? Console.Error);
        return await progress.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExecutionPlan> CreatePlanWithStatusAsync(
        DispatchRequest request,
        CancellationToken cancellationToken)
    {
        TerminalGuiConsoleRenderer.RenderPlanningStatus(Console.Out);
        return await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExecutionPlan> CreatePlanWithDryRunProgressAsync(
        DispatchRequest request,
        CancellationToken cancellationToken)
    {
        var plan = await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
        TerminalGuiConsoleRenderer.RenderDryRunProgressSummary(Console.Out);
        return plan;
    }

    private bool ShouldUseLiveDashboard(bool noDashboard) =>
        displayMode switch
        {
            DispatchRunDisplayMode.LiveDashboard => true,
            DispatchRunDisplayMode.AppendOnly => false,
            _ => !noDashboard && !Console.IsErrorRedirected
        };

    private static async Task RefreshDashboardPeriodicallyAsync(
        Action refresh,
        PeriodicTimer refreshTimer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await refreshTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                refresh();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool IsRootHelpRequest(IReadOnlyList<string> args) =>
        args.Count == 1 && args[0] is "--help" or "-h" or "-?";

    private static bool IsExplicitHelpRequest(IReadOnlyList<string> args) =>
        args.Any(static arg => arg is "--help" or "-h" or "-?");

    private static int RenderRootHelp()
    {
        TerminalGuiConsoleRenderer.RenderRootHelp(Console.Out);
        return 0;
    }

    private static int RenderRunHelp()
    {
        TerminalGuiConsoleRenderer.RenderRunHelp(Console.Out);
        return 0;
    }

    private static int RenderDoctorHelp()
    {
        TerminalGuiConsoleRenderer.RenderDoctorHelp(Console.Out);
        return 0;
    }

    private int RunDoctorCommand()
    {
        var report = doctor.Run();
        TerminalGuiConsoleRenderer.RenderDoctorReport(Console.Out, report);
        return report.Succeeded ? 0 : 1;
    }

    private static int RenderUnknownCommand(string command)
    {
        TerminalGuiConsoleRenderer.RenderError(
            Console.Error,
            "Unknown Dispatch Command",
            $"'{command}' is not a Dispatch command.");
        return 1;
    }
}
