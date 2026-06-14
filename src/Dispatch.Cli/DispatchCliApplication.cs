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
            var plan = await CreatePlanWithStatusAsync(command!.ToRequest(), cancellationToken).ConfigureAwait(false);
            if (command.DryRun)
            {
                await DispatchConsoleRenderer.RenderPlanningProgressAsync(
                    CreateOutputConsole(Console.Out),
                    cancellationToken).ConfigureAwait(false);
                DispatchConsoleRenderer.RenderDryRunPlan(CreateOutputConsole(Console.Out), plan);
                return 0;
            }

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
        while (!cancellationToken.IsCancellationRequested)
        {
            DispatchConsoleRenderer.RenderInteractiveStart(console);
            var action = console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[steelblue1]Command Center[/]")
                    .PageSize(4)
                    .MoreChoicesText("[grey]Move to select an action[/]")
                    .AddChoices(
                        "Start script run",
                        "Run doctor diagnostics",
                        "View command help",
                        "Exit"));

            switch (action)
            {
                case "Start script run":
                    return await RunInteractiveJobAsync(console, cancellationToken).ConfigureAwait(false);
                case "Run doctor diagnostics":
                    DispatchConsoleRenderer.RenderDoctorReport(console, doctor.Run());
                    WaitForMenuReturn(console);
                    break;
                case "View command help":
                    DispatchConsoleRenderer.RenderRunHelp(console);
                    DispatchConsoleRenderer.RenderDoctorHelp(console);
                    WaitForMenuReturn(console);
                    break;
                case "Exit":
                    return 0;
            }
        }

        return 1;
    }

    private async Task<int> RunInteractiveJobAsync(IAnsiConsole console, CancellationToken cancellationToken)
    {
        DispatchConsoleRenderer.RenderRunSetupStart(console);

        var scriptPath = PromptRequired(console, "Script path");
        var computerNames = PromptRequired(console, "Computer name(s)");
        var transport = console.Prompt(
            new SelectionPrompt<string>()
                .Title("Transport")
                .AddChoices(PsExecTransportDescriptor.TransportName, "psrp", "winrm"));
        var runAsSystem = console.Confirm("Run as local SYSTEM?", defaultValue: false);
        var dryRun = console.Confirm("Dry run only?", defaultValue: true);
        var throttle = PromptOptionalInt(console, "Throttle");
        var expectedExitCodes = PromptOptional(console, "Expected exit code(s)", "0");
        var artifactPaths = PromptOptional(console, "Artifact path(s)", string.Empty);
        var outputRoot = PromptOptional(console, "Output root", string.Empty);
        var remoteRoot = PromptOptional(console, "Remote root", string.Empty);
        var scriptArgs = PromptOptional(console, "Script arguments", string.Empty);

        DispatchConsoleRenderer.RenderInteractiveReview(
            console,
            scriptPath,
            computerNames,
            transport,
            runAsSystem,
            dryRun,
            throttle,
            expectedExitCodes,
            artifactPaths);

        if (!console.Confirm("Start Dispatch run?", defaultValue: dryRun))
        {
            DispatchConsoleRenderer.RenderError(
                CreateErrorConsole(Console.Error),
                "Dispatch Run Cancelled",
                "The interactive run was cancelled before planning or endpoint work started.");
            return 1;
        }

        var args = BuildInteractiveRunArguments(
            dryRun,
            scriptPath,
            computerNames,
            transport,
            expectedExitCodes,
            throttle,
            runAsSystem,
            outputRoot,
            remoteRoot,
            artifactPaths,
            scriptArgs);

        return await RunCommandAsync(args, cancellationToken).ConfigureAwait(false);
    }

    private static void WaitForMenuReturn(IAnsiConsole console) =>
        console.Prompt(
            new TextPrompt<string>("[grey]Press Enter to return to the command center[/]")
                .AllowEmpty());

    private static string PromptRequired(IAnsiConsole console, string title) =>
        console.Prompt(
            new TextPrompt<string>(title)
                .Validate(static value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("A value is required.")
                    : ValidationResult.Success()));

    private static string PromptOptional(IAnsiConsole console, string title, string defaultValue) =>
        console.Prompt(
            new TextPrompt<string>(title)
                .DefaultValue(defaultValue)
                .AllowEmpty());

    private static int? PromptOptionalInt(IAnsiConsole console, string title)
    {
        var value = PromptOptional(console, title, string.Empty);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string[] BuildInteractiveRunArguments(
        bool dryRun,
        string scriptPath,
        string computerNames,
        string transport,
        string expectedExitCodes,
        int? throttle,
        bool runAsSystem,
        string outputRoot,
        string remoteRoot,
        string artifactPaths,
        string scriptArgs)
    {
        var args = new List<string>();
        if (dryRun)
        {
            args.Add("--dry-run");
        }

        args.AddRange(["--script", scriptPath, "--computer-name", computerNames, "--transport", transport]);
        AddOptionalPair(args, "--expected-exit-code", expectedExitCodes);
        if (throttle.HasValue)
        {
            args.AddRange(["--throttle", throttle.Value.ToString()]);
        }

        if (runAsSystem)
        {
            args.Add("--run-as-system");
        }

        AddOptionalPair(args, "--output-root", outputRoot);
        AddOptionalPair(args, "--remote-root", remoteRoot);
        AddOptionalPair(args, "--artifact-path", artifactPaths);
        AddScriptArgs(args, scriptArgs);
        return [.. args];
    }

    private static void AddOptionalPair(List<string> args, string option, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            args.AddRange([option, value]);
        }
    }

    private static void AddScriptArgs(List<string> args, string scriptArgs)
    {
        if (string.IsNullOrWhiteSpace(scriptArgs))
        {
            return;
        }

        args.Add("--");
        args.AddRange(scriptArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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
