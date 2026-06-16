using Dispatch.Core;
using Dispatch.Core.Models;
using Spectre.Console;

namespace Dispatch.Cli;

internal static class SpectreConsoleRenderer
{
    private static readonly (string Command, string Description)[] RootCommands =
    [
        ("apply", "Run a YAML job"),
        ("run", "Run an ad-hoc script or command"),
        ("push", "Copy files or scripts to target hosts"),
        ("hosts", "Inspect, validate, and test host files"),
        ("logs", "Inspect run history and output"),
        ("creds", "Manage credential references"),
        ("doctor", "Validate local configuration"),
        ("init", "Create starter files"),
        ("version", "Print version and build information")
    ];

    public static void RenderRootHelp(TextWriter writer)
    {
        var console = CreateConsole(writer);
        console.MarkupLine("[bold]Dispatch[/]");
        console.WriteLine("Windows-native automation runner for PowerShell scripts; PsExec executes today, WinRM-based transports are planned.");
        console.WriteLine();
        console.WriteLine("Usage:");
        console.WriteLine("  dispatch <command> [arguments] [options]");
        console.WriteLine();
        console.Write(CreateCommandTable());
        console.WriteLine();
        console.WriteLine("Global options:");
        console.WriteLine("  -i, --inventory <path>      Host file");
        console.WriteLine("  -t, --target <selector>     Target host/group/selector");
        console.WriteLine("      --config <path>         Dispatch config file");
        console.WriteLine("      --exclude <selector>    Exclude selected hosts");
        console.WriteLine("      --transport <name>      auto, psrp, winrm, psexec");
        console.WriteLine("      --output <format>       rich, table, json, ndjson, yaml");
        console.WriteLine("      --no-color              Disable ANSI color");
        console.WriteLine("      --no-progress           Disable live progress");
        console.WriteLine("  -v, --verbose               Show more detail");
        console.WriteLine("  -h, --help                  Show help");
        console.WriteLine();
        console.WriteLine("Examples:");
        console.WriteLine(@"  dispatch run ps .\scripts\Collect-Disk.ps1 --target web");
        console.WriteLine(@"  dispatch doctor --transport psexec");
    }

    public static void RenderVersion(TextWriter writer)
    {
        var console = CreateConsole(writer);
        console.MarkupLine("[bold]Dispatch[/]");
        console.WriteLine($"Version: {DispatchProduct.Version}");
        console.WriteLine("Command service: Spectre.Console.Cli design");
    }

    public static void RenderRunHelp(TextWriter writer)
    {
        var console = CreateConsole(writer);
        console.MarkupLine("[bold]dispatch run[/]");
        console.WriteLine("Run an ad-hoc script or command.");
        console.WriteLine();
        console.WriteLine("Usage:");
        console.WriteLine("  dispatch run ps <script.ps1> [options]");
        console.WriteLine("  dispatch run cmd <command> [options]");
        console.WriteLine("  dispatch run exe <path> [-- <args>]");
        console.WriteLine("  Current execution support: run ps only");
        console.WriteLine();
        console.WriteLine("Progress options:");
        console.WriteLine("      --no-progress          Disable live progress rendering");
        console.WriteLine("      --no-dashboard         Compatibility alias for --no-progress");
        console.WriteLine("      --no-color             Disable ANSI color");
        console.WriteLine("      --quiet                Suppress rich non-error output");
        console.WriteLine("  -v, --verbose              Accept verbose output mode for current command path");
        console.WriteLine("      --trace                Accept trace output mode for current command path");
        console.WriteLine("      --output <format>      rich, table, json, ndjson, yaml");
        console.WriteLine("      -i, --inventory <path> Host inventory");
        console.WriteLine("      -t, --target <selector> Target host/group/selector");
        console.WriteLine("      --config <path>        Dispatch config file");
        console.WriteLine("      --exclude <selector>   Exclude selected hosts");
        console.WriteLine();
        console.WriteLine("Examples:");
        console.WriteLine(@"  dispatch run ps .\scripts\Collect-Disk.ps1 --target web -i .\hosts\prod.yml");
        console.WriteLine();
        console.WriteLine("Compatibility:");
        console.WriteLine(@"  dispatch run --script .\Fix.ps1 --computer-name PC001 --transport psexec");
    }

    public static void RenderDoctorHelp(TextWriter writer)
    {
        var console = CreateConsole(writer);
        console.MarkupLine("[bold]dispatch doctor[/]");
        console.WriteLine("Validate local configuration and dependencies.");
        console.WriteLine();
        console.WriteLine("Usage:");
        console.WriteLine("  dispatch doctor [--transport psexec|psrp|winrm|auto]");
    }

    public static void RenderPlannedFeature(TextWriter writer, string command, string roadmapItem)
    {
        var console = CreateConsole(writer);
        console.MarkupLine("[red]Planned Dispatch command[/]");
        console.WriteLine($"'{command}' is part of the documented CLI design but is not implemented yet.");
        console.WriteLine($"Roadmap item: {roadmapItem}");
    }

    public static void RenderError(TextWriter writer, string title, string message)
    {
        var console = CreateConsole(writer);
        console.MarkupLine($"[red]{Markup.Escape(title)}[/]");
        foreach (var line in message.Split(Environment.NewLine))
        {
            console.WriteLine(line);
        }
    }

    public static async Task<ExecutionPlan> RunPlanningStatusAsync(
        TextWriter writer,
        Func<CancellationToken, Task<ExecutionPlan>> createPlan,
        bool noColor,
        CancellationToken cancellationToken)
    {
        if (Console.IsOutputRedirected)
        {
            RenderPlanningStatus(writer);
            return await createPlan(cancellationToken).ConfigureAwait(false);
        }

        var console = CreateInteractiveConsole(writer, noColor);
        return await console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(
                "Building Dispatch execution plan",
                async context =>
                {
                    context.Status("Validating request and resolving targets");
                    return await createPlan(cancellationToken).ConfigureAwait(false);
                })
            .ConfigureAwait(false);
    }

    public static async Task<ExecutionPlan> RunDryRunPlanningProgressAsync(
        TextWriter writer,
        Func<CancellationToken, Task<ExecutionPlan>> createPlan,
        bool noColor,
        CancellationToken cancellationToken)
    {
        if (Console.IsOutputRedirected)
        {
            var plan = await createPlan(cancellationToken).ConfigureAwait(false);
            RenderDryRunProgressSummary(writer);
            return plan;
        }

        var console = CreateInteractiveConsole(writer, noColor);
        return await console.Progress()
            .Columns(
            [
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn()
            ])
            .StartAsync(async context =>
            {
                var validate = context.AddTask("Validate request").MaxValue(1);
                validate.Increment(1);

                var build = context.AddTask("Build execution plan").MaxValue(1);
                var plan = await createPlan(cancellationToken).ConfigureAwait(false);
                build.Increment(1);

                var layout = context.AddTask("Resolve target layout").MaxValue(1);
                layout.Increment(1);

                var output = context.AddTask("Prepare plan output").MaxValue(1);
                output.Increment(1);

                return plan;
            })
            .ConfigureAwait(false);
    }

    public static void RenderDryRunProgressSummary(TextWriter writer)
    {
        var console = CreateConsole(writer);
        console.MarkupLine("[bold]Dry run planning[/]");
        console.WriteLine("PASS  Validate request");
        console.WriteLine("PASS  Build execution plan");
        console.WriteLine("PASS  Resolve target layout");
        console.WriteLine("PASS  Prepare plan output");
    }

    public static void RenderPlanningStatus(TextWriter writer)
    {
        var console = CreateConsole(writer);
        console.MarkupLine("[bold]Planning[/]");
        console.WriteLine("Building Dispatch execution plan through the shared planner.");
    }

    public static void RenderDryRunPlan(TextWriter writer, ExecutionPlan plan)
    {
        var console = CreateConsole(writer);
        console.MarkupLine("[bold]Dispatch plan[/]");
        console.WriteLine($"Run ID: {plan.RunId}");
        console.WriteLine($"Transport: {plan.Job.Transport}");
        console.WriteLine($"Payload: {plan.Job.Payload.DisplayName}");
        console.WriteLine($"Targets: {plan.Targets.Count}");
        console.WriteLine($"Throttle: {plan.ThrottleLimit}");
        console.WriteLine();
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Target");
        table.AddColumn("State");
        table.AddColumn("Remote script");
        foreach (var target in plan.Targets)
        {
            table.AddRow(
                Markup.Escape(target.Target.Name),
                Markup.Escape(target.State.ToString()),
                Markup.Escape(target.PlannedRemoteScriptPath ?? "Pending"));
        }

        console.Write(table);
        console.WriteLine();
        console.WriteLine($"Local run root: {plan.LocalRunRoot}");
        console.WriteLine($"Admin results: {plan.LocalResultsJsonPath}");
        console.WriteLine($"CSV results: {plan.LocalResultsCsvPath}");
    }

    public static void RenderRunResult(TextWriter writer, DispatchRunResult result)
    {
        var console = CreateConsole(writer);
        console.MarkupLine(result.FailedCount == 0 && result.TimedOutCount == 0 && result.CancelledCount == 0
            ? "[green]Dispatch run complete[/]"
            : "[red]Dispatch run completed with failures[/]");
        console.WriteLine($"Run ID: {result.RunId}");
        console.WriteLine($"Transport: {result.Transport}");
        console.WriteLine($"Targets: {result.TargetCount}");
        console.WriteLine($"Succeeded: {result.SuccessCount}");
        console.WriteLine($"Failed: {result.FailedCount}");
        console.WriteLine($"Timed Out: {result.TimedOutCount}");
        console.WriteLine($"Cancelled: {result.CancelledCount}");
        console.WriteLine();
        console.Write(CreateResultTable(result.Targets));
        console.WriteLine();
        console.WriteLine($"Result file: {result.ResultPath}");
    }

    public static void RenderDoctorReport(TextWriter writer, DispatchDoctorReport report)
    {
        var console = CreateConsole(writer);
        console.MarkupLine(report.Succeeded ? "[green]Dispatch doctor passed[/]" : "[red]Dispatch doctor failed[/]");
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Status");
        table.AddColumn("Check");
        table.AddColumn("Message");
        foreach (var check in report.Checks)
        {
            table.AddRow(
                Markup.Escape(FormatDoctorStatus(check.Status)),
                Markup.Escape(check.Name),
                Markup.Escape(string.IsNullOrWhiteSpace(check.Detail) ? check.Message : $"{check.Message} {check.Detail}"));
        }

        console.Write(table);
    }

    internal static IAnsiConsole CreateConsole(TextWriter writer) =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer)
        });

    internal static IAnsiConsole CreateInteractiveConsole(TextWriter writer, bool noColor = false) =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = noColor ? AnsiSupport.No : AnsiSupport.Detect,
            ColorSystem = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            Interactive = InteractionSupport.Detect,
            Out = new AnsiConsoleOutput(writer)
        });

    private static Table CreateCommandTable()
    {
        var table = new Table().Border(TableBorder.None);
        table.AddColumn("Command");
        table.AddColumn("Description");
        foreach (var (command, description) in RootCommands)
        {
            table.AddRow(command, description);
        }

        return table;
    }

    private static Table CreateResultTable(IEnumerable<TargetExecutionResult> targets)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Target");
        table.AddColumn("Status");
        table.AddColumn("Exit");
        table.AddColumn("Failure");
        foreach (var target in targets)
        {
            var failure = target.FailureCategory == FailureCategory.None
                ? "-"
                : $"{target.FailureCategory}: {target.FailureMessage}";
            table.AddRow(
                Markup.Escape(target.Target),
                Markup.Escape(target.State.ToString()),
                Markup.Escape(target.ExitCode?.ToString() ?? "-"),
                Markup.Escape(failure));
        }

        return table;
    }

    private static string FormatDoctorStatus(DispatchDoctorStatus status) =>
        status switch
        {
            DispatchDoctorStatus.Pass => "PASS",
            DispatchDoctorStatus.Warning => "WARN",
            DispatchDoctorStatus.Fail => "FAIL",
            _ => status.ToString().ToUpperInvariant()
        };
}
