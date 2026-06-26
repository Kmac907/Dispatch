using Dispatch.Core;
using Dispatch.Core.Credentials;
using Dispatch.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

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
        console.WriteLine("Windows-native automation runner for PowerShell scripts and commands; PsExec, raw WinRM, and PSRP execute today, with raw WinRM and PSRP live-validated on the current approved hosts.");
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
        console.WriteLine("  dispatch doctor");
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
        console.WriteLine("  Current execution support: run ps through psexec, winrm, or psrp; run cmd and run exe through winrm or psrp");
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
        console.WriteLine("      --credential <name>    Credential reference override");
        console.WriteLine("      --secret name=reference Script secret reference for run ps plan/dry-run handoff");
        console.WriteLine("      --exclude <selector>   Exclude selected hosts");
        console.WriteLine();
        console.WriteLine("Examples:");
        console.WriteLine(@"  dispatch run ps .\scripts\Collect-Disk.ps1 --target web -i .\hosts\prod.yml");
        console.WriteLine(@"  dispatch run cmd whoami --target PC001 --transport psrp");
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
        console.WriteLine("  dispatch doctor");
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
        if (plan.Job.ScriptSecrets.Count > 0)
        {
            console.WriteLine($"Script secrets: {plan.Job.ScriptSecrets.Count} redacted parameter handoff plan(s)");
        }

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
        console.WriteLine($"Event log: {plan.LocalEventsNdjsonPath}");
        if (plan.Job.ScriptSecrets.Count > 0)
        {
            console.WriteLine("Script secret handoff:");
            foreach (var secret in plan.Job.ScriptSecrets)
            {
                console.WriteLine($"  {secret.Name} -> {secret.ReferenceName} as {secret.ScriptParameterName} {secret.RedactedValue}");
            }
        }

        if (plan.Job.ResultPolicy.WriteCsv)
        {
            console.WriteLine($"Optional CSV export: {plan.LocalResultsCsvPath}");
        }
    }

    public static void RenderRunResult(TextWriter writer, DispatchRunResult result)
    {
        var console = CreateConsole(writer);
        var resultFilePath = string.IsNullOrWhiteSpace(result.ResultPath) ? "-" : result.ResultPath;
        var eventFilePath = TryGetEventFilePath(result.ResultPath) ?? "-";
        var targetRootPattern = TryGetTargetRootPattern(result.ResultPath) ?? TryGetTargetRootFromStdout(result.Targets) ?? "-";
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
        console.Write(CreateOutputSummaryPanel(resultFilePath, eventFilePath, targetRootPattern));
        console.WriteLine();
    }

    public static void RenderRunHistory(TextWriter writer, string localRunRoot, IReadOnlyList<DispatchRunHistoryEntry> runs)
    {
        var console = CreateConsole(writer);
        console.MarkupLine("[bold]Dispatch runs[/]");
        console.WriteLine($"Local run root: {localRunRoot}");
        console.WriteLine();
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Run");
        table.AddColumn("Started");
        table.AddColumn("Transport");
        table.AddColumn("Payload");
        table.AddColumn("Targets");
        table.AddColumn("Succeeded");
        table.AddColumn("Failed");
        table.AddColumn("Timed Out");
        foreach (var run in runs)
        {
            table.AddRow(
                Markup.Escape(run.RunId),
                Markup.Escape(run.StartedAt.ToString("u")),
                Markup.Escape(run.Transport.ToString()),
                Markup.Escape(run.PayloadName),
                Markup.Escape(run.TargetCount.ToString()),
                Markup.Escape(run.SuccessCount.ToString()),
                Markup.Escape(run.FailedCount.ToString()),
                Markup.Escape(run.TimedOutCount.ToString()));
        }

        console.Write(table);
    }

    public static void RenderRunEventTail(TextWriter writer, DispatchRunEventTail tail)
    {
        var console = CreateConsole(writer);
        console.MarkupLine("[bold]Dispatch log tail[/]");
        console.WriteLine($"Run ID: {tail.RunId}");
        console.WriteLine($"Event file: {tail.EventPath}");
        console.WriteLine($"Events: {tail.Events.Count}");
        console.WriteLine();

        if (tail.Events.Count == 0)
        {
            console.WriteLine("No events were found in the durable event stream.");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Time");
        table.AddColumn("Type");
        table.AddColumn("Target");
        table.AddColumn("State");
        table.AddColumn("Message");

        foreach (var entry in tail.Events)
        {
            table.AddRow(
                Markup.Escape(entry.Timestamp?.ToString("u") ?? "-"),
                Markup.Escape(entry.Type),
                Markup.Escape(entry.Target ?? "-"),
                Markup.Escape(entry.State ?? "-"),
                Markup.Escape(entry.Message ?? "-"));
        }

        console.Write(table);
    }

    public static void RenderRunLogExport(TextWriter writer, DispatchRunLogExportResult result)
    {
        var console = CreateConsole(writer);
        console.MarkupLine("[bold]Dispatch log export[/]");
        console.WriteLine($"Run ID: {result.RunId}");
        console.WriteLine($"Export root: {result.ExportRoot}");
        console.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("File");
        table.AddColumn("Path");
        table.AddRow("Results", Markup.Escape(result.ResultsJsonPath));
        table.AddRow("Events", Markup.Escape(result.EventsNdjsonPath ?? "-"));
        table.AddRow("CSV", Markup.Escape(result.ResultsCsvPath));
        console.Write(table);
    }

    public static void RenderRunRetryPlan(TextWriter writer, DispatchRunRetryPlan retryPlan)
    {
        var console = CreateConsole(writer);
        console.MarkupLine("[bold]Dispatch retry plan[/]");
        console.WriteLine($"Run ID: {retryPlan.RunId}");
        console.WriteLine($"Transport: {retryPlan.Transport}");
        console.WriteLine($"Payload: {retryPlan.PayloadType} {retryPlan.PayloadName}");
        console.WriteLine($"Retry targets: {retryPlan.RetryTargetCount}");
        console.WriteLine($"Automatic re-execution: {(retryPlan.ReexecutionSupported ? "manual command available" : "not available")}");
        console.WriteLine(Markup.Escape(retryPlan.Message));
        if (!string.IsNullOrWhiteSpace(retryPlan.SuggestedCommand))
        {
            console.WriteLine();
            console.WriteLine("Suggested command:");
            console.WriteLine(retryPlan.SuggestedCommand);
        }

        if (retryPlan.Targets.Count == 0)
        {
            return;
        }

        console.WriteLine();
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Target");
        table.AddColumn("State");
        table.AddColumn("Failure");
        table.AddColumn("Exit");
        table.AddColumn("Message");
        foreach (var target in retryPlan.Targets)
        {
            table.AddRow(
                Markup.Escape(target.Target),
                Markup.Escape(target.State.ToString()),
                Markup.Escape(target.FailureCategory.ToString()),
                Markup.Escape(target.ExitCode?.ToString() ?? "-"),
                Markup.Escape(target.FailureMessage ?? "-"));
        }

        console.Write(table);
    }

    public static void RenderCredentialOperation(TextWriter writer, CredentialProviderOperationResult result)
    {
        var console = CreateConsole(writer);
        console.MarkupLine("[bold]Dispatch credentials[/]");
        console.WriteLine($"Provider: {result.ProviderName}");
        console.WriteLine(result.Message);

        if (result.References.Count == 0)
        {
            return;
        }

        console.WriteLine();
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name");
        table.AddColumn("User");
        foreach (var reference in result.References)
        {
            table.AddRow(
                Markup.Escape(reference.Name),
                Markup.Escape(reference.UserName ?? "-"));
        }

        console.Write(table);
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

    private static IRenderable CreateOutputSummaryPanel(string resultFilePath, string eventFilePath, string targetRootPattern)
    {
        var stdoutPath = string.IsNullOrWhiteSpace(targetRootPattern) || targetRootPattern == "-"
            ? "-"
            : Path.Combine(targetRootPattern, "stdout.txt");
        var stderrPath = string.IsNullOrWhiteSpace(targetRootPattern) || targetRootPattern == "-"
            ? "-"
            : Path.Combine(targetRootPattern, "stderr.txt");

        var lines = new[]
        {
            $"[bold]Results[/]: {Markup.Escape(resultFilePath)}",
            $"[bold]Events[/]: {Markup.Escape(eventFilePath)}",
            $"[bold]Target Root[/]: {Markup.Escape(targetRootPattern)}",
            $"[bold]Stdout[/]: {Markup.Escape(stdoutPath)}",
            $"[bold]Stderr[/]: {Markup.Escape(stderrPath)}"
        };

        return new Panel(new Rows(lines.Select(static line => (IRenderable)new Markup(line)).ToArray()))
            .Header("Outputs")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private static string FormatDoctorStatus(DispatchDoctorStatus status) =>
        status switch
        {
            DispatchDoctorStatus.Pass => "PASS",
            DispatchDoctorStatus.Warning => "WARN",
            DispatchDoctorStatus.Fail => "FAIL",
            _ => status.ToString().ToUpperInvariant()
        };

    private static string? TryGetEventFilePath(string? resultPath)
    {
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            return null;
        }

        var adminRoot = Path.GetDirectoryName(resultPath);
        return string.IsNullOrWhiteSpace(adminRoot)
            ? null
            : Path.Combine(adminRoot, "events.ndjson");
    }

    private static string? TryGetTargetRootPattern(string? resultPath)
    {
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            return null;
        }

        var adminRoot = Path.GetDirectoryName(resultPath);
        var runRoot = string.IsNullOrWhiteSpace(adminRoot) ? null : Path.GetDirectoryName(adminRoot);
        return string.IsNullOrWhiteSpace(runRoot)
            ? null
            : Path.Combine(runRoot, "Targets", "<target>");
    }

    private static string? TryGetTargetRootFromStdout(IEnumerable<TargetExecutionResult> targets)
    {
        var stdoutPath = targets
            .Select(static target => target.StdoutPath)
            .FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path));

        return string.IsNullOrWhiteSpace(stdoutPath) ? null : Path.GetDirectoryName(stdoutPath);
    }

}
