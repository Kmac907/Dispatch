using Dispatch.Core;
using Dispatch.Core.Configuration;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Dispatch.Transports.PsExec;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace Dispatch.Cli;

public sealed class DispatchCliApplication(
    IOptions<DispatchOptions> options,
    IDispatchPlanner planner,
    IDispatchExecutor executor,
    IDispatchDoctor doctor,
    DispatchRunDisplayMode displayMode = DispatchRunDisplayMode.Auto,
    IAnsiConsole? statusConsole = null)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Any(static arg => arg is "--version" or "-v"))
        {
            DispatchConsoleRenderer.RenderVersion(CreateOutputConsole(Console.Out));
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
            DispatchConsoleRenderer.RenderError(CreateErrorConsole(Console.Error), "Invalid Dispatch Command", error);
            return 1;
        }

        try
        {
            var request = command!.ToRequest();
            if (command.DryRun)
            {
                var dryRunPlan = await CreatePlanWithDryRunProgressAsync(request, cancellationToken).ConfigureAwait(false);
                DispatchConsoleRenderer.RenderDryRunPlan(CreateOutputConsole(Console.Out), dryRunPlan);
                return 0;
            }

            var plan = await CreatePlanWithStatusAsync(request, cancellationToken).ConfigureAwait(false);
            var result = ShouldUseLiveDashboard(command.NoDashboard)
                ? await RunWithLiveDashboardAsync(plan, cancellationToken).ConfigureAwait(false)
                : await RunWithCompactProgressAsync(plan, cancellationToken).ConfigureAwait(false);
            DispatchConsoleRenderer.RenderRunResult(CreateOutputConsole(Console.Out), result);
            return result.FailedCount == 0 && result.TimedOutCount == 0 && result.CancelledCount == 0 ? 0 : 1;
        }
        catch (DispatchPlanningException exception)
        {
            var message = string.Join(
                Environment.NewLine,
                exception.Errors.Select(static validationError => $"{validationError.Code}: {validationError.Message}"));
            DispatchConsoleRenderer.RenderError(CreateErrorConsole(Console.Error), "Dispatch Planning Failed", message);

            return 1;
        }
    }

    private async Task<int> RunInteractiveAsync(CancellationToken cancellationToken)
    {
        var console = CreateOutputConsole(Console.Out);
        var commandCenter = new SpectreDispatchCommandCenter(
            console,
            doctor,
            static () => Console.ReadKey(intercept: true));
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
        var dashboard = new SpectreDispatchRunDashboard(plan, DateTimeOffset.UtcNow);
        var console = statusConsole ?? CreateStatusConsole(Console.Error);

        try
        {
            return await console
                .Live(dashboard.Render())
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .Cropping(VerticalOverflowCropping.Bottom)
                .StartAsync(async context =>
                {
                    using var refreshTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                    using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var refreshGate = new object();
                    var refreshTask = RefreshDashboardPeriodicallyAsync(
                        dashboard,
                        context,
                        refreshGate,
                        refreshTimer,
                        refreshCancellation.Token);
                    var observer = new SpectreDispatchExecutionObserver(dashboard, context, refreshGate);

                    try
                    {
                        var result = await executor.ExecuteAsync(plan, observer, cancellationToken).ConfigureAwait(false);
                        dashboard.Complete(result);
                        lock (refreshGate)
                        {
                            context.UpdateTarget(dashboard.Render());
                            context.Refresh();
                        }

                        return result;
                    }
                    finally
                    {
                        await refreshCancellation.CancelAsync().ConfigureAwait(false);
                        await refreshTask.ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
        }
        catch (IOException exception) when (displayMode == DispatchRunDisplayMode.Auto)
        {
            DispatchConsoleRenderer.RenderError(
                CreateErrorConsole(Console.Error),
                "Live Dashboard Unavailable",
                $"Dispatch is using compact live progress for this run. {exception.Message}");
            return await RunWithCompactProgressAsync(plan, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DispatchRunResult> RunWithCompactProgressAsync(
        ExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        if (Console.IsErrorRedirected && statusConsole is null)
        {
            return await executor.ExecuteAsync(plan, NullDispatchExecutionObserver.Instance, cancellationToken)
                .ConfigureAwait(false);
        }

        var console = statusConsole ?? CreateStatusConsole(Console.Error);
        var progress = new SpectreCompactDispatchProgress(plan, executor, console);
        return await progress.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExecutionPlan> CreatePlanWithStatusAsync(
        DispatchRequest request,
        CancellationToken cancellationToken)
    {
        if (Console.IsOutputRedirected)
        {
            return await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await CreateOutputConsole(Console.Out)
            .Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("steelblue1"))
            .StartAsync("Building Dispatch execution plan", async _ =>
                await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private async Task<ExecutionPlan> CreatePlanWithDryRunProgressAsync(
        DispatchRequest request,
        CancellationToken cancellationToken)
    {
        if (Console.IsOutputRedirected)
        {
            var redirectedPlan = await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
            DispatchConsoleRenderer.RenderDryRunProgressSummary(CreateOutputConsole(Console.Out));
            return redirectedPlan;
        }

        var plan = await CreateOutputConsole(Console.Out)
            .Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
            [
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(Spinner.Known.Dots)
            ])
            .StartAsync(async context =>
            {
                var validate = context.AddTask("[steelblue1]Validate dry-run request[/]");
                validate.Increment(100);
                await HoldProgressFrameAsync(cancellationToken).ConfigureAwait(false);

                var planTask = context.AddTask("[steelblue1]Build execution plan[/]");
                planTask.Increment(15);
                var plan = await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
                planTask.Increment(85);
                await HoldProgressFrameAsync(cancellationToken).ConfigureAwait(false);

                var targetTask = context.AddTask("[steelblue1]Resolve target layout[/]");
                targetTask.Increment(100);
                await HoldProgressFrameAsync(cancellationToken).ConfigureAwait(false);

                var renderTask = context.AddTask("[steelblue1]Prepare dry-run view[/]");
                renderTask.Increment(100);
                await HoldProgressFrameAsync(cancellationToken).ConfigureAwait(false);

                return plan;
            })
            .ConfigureAwait(false);
        DispatchConsoleRenderer.RenderDryRunProgressSummary(CreateOutputConsole(Console.Out));
        return plan;
    }

    private static async Task HoldProgressFrameAsync(CancellationToken cancellationToken) =>
        await Task.Delay(TimeSpan.FromMilliseconds(75), cancellationToken).ConfigureAwait(false);

    private bool ShouldUseLiveDashboard(bool noDashboard) =>
        displayMode switch
        {
            DispatchRunDisplayMode.LiveDashboard => true,
            DispatchRunDisplayMode.AppendOnly => false,
            _ => !noDashboard && !Console.IsErrorRedirected
        };

    private static async Task RefreshDashboardPeriodicallyAsync(
        SpectreDispatchRunDashboard dashboard,
        LiveDisplayContext context,
        object gate,
        PeriodicTimer refreshTimer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await refreshTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (gate)
                {
                    context.UpdateTarget(dashboard.Render());
                    context.Refresh();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static IAnsiConsole CreateStatusConsole(TextWriter writer) =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new DispatchAnsiConsoleOutput(writer, isTerminal: true),
            Interactive = InteractionSupport.Yes
        });

    private static IAnsiConsole CreateOutputConsole(TextWriter writer) =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new DispatchAnsiConsoleOutput(writer, !Console.IsOutputRedirected),
            Interactive = InteractionSupport.Detect
        });

    private static IAnsiConsole CreateErrorConsole(TextWriter writer) =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new DispatchAnsiConsoleOutput(writer, !Console.IsErrorRedirected),
            Interactive = InteractionSupport.Detect
        });

    private static bool IsRootHelpRequest(IReadOnlyList<string> args) =>
        args.Count == 1 && args[0] is "--help" or "-h" or "-?";

    private static bool IsExplicitHelpRequest(IReadOnlyList<string> args) =>
        args.Any(static arg => arg is "--help" or "-h" or "-?");

    private static int RenderRootHelp()
    {
        DispatchConsoleRenderer.RenderRootHelp(CreateOutputConsole(Console.Out));
        return 0;
    }

    private static int RenderRunHelp()
    {
        DispatchConsoleRenderer.RenderRunHelp(CreateOutputConsole(Console.Out));
        return 0;
    }

    private static int RenderDoctorHelp()
    {
        DispatchConsoleRenderer.RenderDoctorHelp(CreateOutputConsole(Console.Out));
        return 0;
    }

    private int RunDoctorCommand()
    {
        var report = doctor.Run();
        DispatchConsoleRenderer.RenderDoctorReport(CreateOutputConsole(Console.Out), report);
        return report.Succeeded ? 0 : 1;
    }

    private static int RenderUnknownCommand(string command)
    {
        DispatchConsoleRenderer.RenderError(
            CreateErrorConsole(Console.Error),
            "Unknown Dispatch Command",
            $"'{command}' is not a Dispatch command.");
        return 1;
    }
}
