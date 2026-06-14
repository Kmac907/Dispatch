using Dispatch.Core;
using Dispatch.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Dispatch.Cli;

internal static class DispatchConsoleRenderer
{
    public static void RenderHome(IAnsiConsole console)
    {
        console.Write(CreateShell(
            "[bold steelblue1]Dispatch Command Center[/]",
            new Rows(
                new Rule("[grey]Command Service[/]").RuleStyle("grey"),
                CreateHeroGrid(),
                CreateCommandTable(),
                CreateCapabilityBreakdown(),
                CreateNextStepPanel())));
    }

    public static void RenderVersion(IAnsiConsole console)
    {
        var table = new Table().NoBorder().HideHeaders();
        table.AddColumn("Name");
        table.AddColumn("Value");
        table.AddRow("[grey]Product[/]", "Dispatch");
        table.AddRow("[grey]Version[/]", Markup.Escape(DispatchProduct.Version));

        console.Write(CreateShell("[bold steelblue1]Dispatch Version[/]", table));
    }

    public static void RenderRootHelp(IAnsiConsole console) => RenderHome(console);

    public static void RenderRunHelp(IAnsiConsole console)
    {
        var options = new Table().RoundedBorder().BorderColor(Color.Grey).Expand();
        options.AddColumn("Option");
        options.AddColumn("Purpose");
        options.AddRow("--script", "PowerShell script path.");
        options.AddRow("--computer-name", "Target name or comma-separated targets.");
        options.AddRow("--target-file", "Target file path.");
        options.AddRow("--transport", "Transport: psexec, psrp, or winrm.");
        options.AddRow("--dry-run", "Render the execution plan without endpoint work.");
        options.AddRow("--no-dashboard", "Use compact live progress bars instead of the full live dashboard.");
        options.AddRow("--expected-exit-code", "Expected code or comma-separated codes.");
        options.AddRow("--throttle", "Maximum concurrent target executions.");
        options.AddRow("--artifact-path", "Relative artifact path or comma-separated paths.");

        console.Write(CreateShell(
            "[bold steelblue1]Run Command[/]",
            new Rows(
                new Markup("[grey]Build and execute a Dispatch job through the shared planner/executor path.[/]"),
                options,
                new Panel(new Markup("[steelblue1]Example[/]\n[grey]dispatch run --dry-run --script .\\Fix.ps1 --computer-name PC001 --transport psexec[/]"))
                    .Header("[grey] Usage [/]")
                    .RoundedBorder()
                    .BorderColor(Color.Grey))));
    }

    public static void RenderDoctorHelp(IAnsiConsole console)
    {
        console.Write(CreateShell(
            "[bold steelblue1]Doctor Command[/]",
            new Rows(
                new Markup("[grey]Validate local prerequisites before endpoint execution.[/]"),
                new Panel(new Markup("Checks Windows host support, PowerShell availability, PsExec path resolution, output-root writability, and admin context."))
                    .Header("[grey] Scope [/]")
                    .RoundedBorder()
                    .BorderColor(Color.Grey))));
    }

    public static void RenderError(IAnsiConsole console, string title, string message)
    {
        var table = new Table().NoBorder().HideHeaders();
        table.AddColumn("Field");
        table.AddColumn("Value");
        table.AddRow("[red]Error[/]", Markup.Escape(message));
        table.AddRow("[grey]Next[/]", "Run a help command to review valid options.");

        console.Write(new Panel(table)
            .Header($"[bold red] {Markup.Escape(title)} [/]")
            .RoundedBorder()
            .BorderColor(Color.Red));
    }

    public static async Task RenderPlanningProgressAsync(IAnsiConsole console, CancellationToken cancellationToken)
    {
        await console.Progress()
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
                var validate = context.AddTask("[steelblue1]Validate request[/]");
                var resolve = context.AddTask("[steelblue1]Resolve targets[/]");
                var paths = context.AddTask("[steelblue1]Plan run layout[/]");
                foreach (var task in new[] { validate, resolve, paths })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    task.Increment(100);
                    await Task.Yield();
                }
            }).ConfigureAwait(false);
    }

    public static void RenderDryRunPlan(IAnsiConsole console, ExecutionPlan plan)
    {
        console.Write(CreateShell(
            "[bold steelblue1]Dispatch Dry Run[/]",
            new Rows(
                new Rule("[grey]Execution Plan[/]").RuleStyle("grey"),
                CreatePlanSummary(plan),
                CreatePlanCharts(plan),
                CreateTargetPlanTable(plan),
                CreatePlanPathTable(plan))));
    }

    public static void RenderRunResult(IAnsiConsole console, DispatchRunResult result)
    {
        var table = new Table().RoundedBorder().BorderColor(GetResultColor(result)).Expand();
        table.AddColumn("Run");
        table.AddColumn("Transport");
        table.AddColumn("Targets");
        table.AddColumn("Succeeded");
        table.AddColumn("Failed");
        table.AddColumn("Timed Out");
        table.AddColumn("Cancelled");
        table.AddRow(
            Markup.Escape(result.RunId),
            Markup.Escape(result.Transport.ToString()),
            result.TargetCount.ToString(),
            $"[green]{result.SuccessCount}[/]",
            result.FailedCount == 0 ? "0" : $"[red]{result.FailedCount}[/]",
            result.TimedOutCount == 0 ? "0" : $"[yellow]{result.TimedOutCount}[/]",
            result.CancelledCount == 0 ? "0" : $"[grey]{result.CancelledCount}[/]");

        console.Write(CreateShell(
            result.FailedCount == 0 && result.TimedOutCount == 0 && result.CancelledCount == 0
                ? "[bold green]Dispatch Run Complete[/]"
                : "[bold red]Dispatch Run Completed With Failures[/]",
            new Rows(
                new Rule("[grey]Run Result[/]").RuleStyle("grey"),
                table,
                CreateResultBreakdown(result),
                CreateResultTargetTable(result),
                new Panel(new Markup(Markup.Escape(result.ResultPath)))
                    .Header("[grey] Result File [/]")
                    .RoundedBorder()
                    .BorderColor(Color.Grey))));
    }

    public static void RenderDoctorReport(IAnsiConsole console, DispatchDoctorReport report)
    {
        var table = new Table().RoundedBorder().BorderColor(report.Succeeded ? Color.Green : Color.Red).Expand();
        table.AddColumn("Status");
        table.AddColumn("Check");
        table.AddColumn("Result");
        foreach (var check in report.Checks)
        {
            table.AddRow(
                FormatDoctorStatus(check.Status),
                Markup.Escape(check.Name),
                Markup.Escape(string.IsNullOrWhiteSpace(check.Detail)
                    ? check.Message
                    : $"{check.Message} {check.Detail}"));
        }

        console.Write(CreateShell(
            report.Succeeded ? "[bold green]Dispatch Doctor Passed[/]" : "[bold red]Dispatch Doctor Failed[/]",
            table));
    }

    private static IRenderable CreateShell(string header, IRenderable body) =>
        new Panel(body)
            .Header($" {header} ")
            .RoundedBorder()
            .BorderColor(Color.SteelBlue1)
            .Expand();

    private static IRenderable CreateHeroGrid()
    {
        var grid = new Grid().Expand();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow(
            new Panel(new Markup("[bold]Windows-native orchestration[/]\n[grey]Plan, execute, monitor, and collect endpoint script runs from one operator console.[/]"))
                .Header("[grey] Mission [/]")
                .RoundedBorder()
                .BorderColor(Color.SteelBlue1),
            new Panel(new Markup($"[bold]Version[/] {Markup.Escape(DispatchProduct.Version)}\n[bold]Mode[/] Spectre.Console command service"))
                .Header("[grey] Runtime [/]")
                .RoundedBorder()
                .BorderColor(Color.Grey));
        return grid;
    }

    private static IRenderable CreateCommandTable()
    {
        var table = new Table().RoundedBorder().BorderColor(Color.Grey).Expand();
        table.AddColumn("Command");
        table.AddColumn("Purpose");
        table.AddRow("dispatch run", "Plan or execute a script job.");
        table.AddRow("dispatch doctor", "Check local prerequisites.");
        table.AddRow("dispatch --version", "Show installed version.");
        return table;
    }

    private static IRenderable CreateCapabilityBreakdown() =>
        new BreakdownChart()
            .Width(60)
            .AddItem("Plan", 25, Color.SteelBlue1)
            .AddItem("Execute", 25, Color.Green)
            .AddItem("Diagnose", 25, Color.Yellow)
            .AddItem("Report", 25, Color.Grey);

    private static IRenderable CreateNextStepPanel() =>
        new Panel(new Markup("[steelblue1]Recommended[/]\nUse [bold]dispatch run --help[/] or start the guided flow with [bold]dispatch[/]."))
            .Header("[grey] Next Step [/]")
            .RoundedBorder()
            .BorderColor(Color.Grey);

    private static IRenderable CreatePlanSummary(ExecutionPlan plan)
    {
        var table = new Table().RoundedBorder().BorderColor(Color.Grey).Expand();
        table.AddColumn("Run");
        table.AddColumn("Transport");
        table.AddColumn("Payload");
        table.AddColumn("Targets");
        table.AddColumn("Throttle");
        table.AddRow(
            Markup.Escape(plan.RunId),
            Markup.Escape(plan.Job.Transport.ToString()),
            Markup.Escape(plan.Job.Payload.DisplayName),
            plan.Targets.Count.ToString(),
            plan.ThrottleLimit.ToString());
        return table;
    }

    private static IRenderable CreateTargetPlanTable(ExecutionPlan plan)
    {
        var table = new Table().RoundedBorder().BorderColor(Color.Grey).Expand();
        table.AddColumn("Target");
        table.AddColumn("State");
        table.AddColumn("Remote Script");
        table.AddColumn("Command");
        foreach (var target in plan.Targets)
        {
            table.AddRow(
                Markup.Escape(target.Target.Name),
                Markup.Escape(target.State.ToString()),
                Markup.Escape(target.PlannedRemoteScriptPath ?? "Pending"),
                Markup.Escape(target.PlannedCommand?.RenderedCommand ?? "Pending"));
        }

        return table;
    }

    private static IRenderable CreatePlanCharts(ExecutionPlan plan)
    {
        var grid = new Grid().Expand();
        grid.AddColumn();
        grid.AddColumn();

        var chart = new BarChart()
            .Width(60)
            .Label("[grey]Planned Work[/]")
            .CenterLabel();
        chart.AddItem("Targets", plan.Targets.Count, Color.SteelBlue1);
        chart.AddItem("Expected Exit Codes", plan.Job.ExpectedExitCodes.Count, Color.Green);
        chart.AddItem("Artifacts", plan.Job.ArtifactPolicy.Paths?.Count ?? 0, Color.Yellow);

        var breakdown = new BreakdownChart()
            .Width(60)
            .AddItem("Script", 50, Color.SteelBlue1)
            .AddItem("Transfer", 20, Color.Yellow)
            .AddItem("Execute", 20, Color.Green)
            .AddItem("Collect", 10, Color.Grey);

        grid.AddRow(chart, breakdown);
        return grid;
    }

    private static IRenderable CreatePlanPathTable(ExecutionPlan plan)
    {
        var table = new Table().RoundedBorder().BorderColor(Color.Grey).Expand();
        table.AddColumn("Path");
        table.AddColumn("Value");
        table.AddRow("Local run root", Markup.Escape(plan.LocalRunRoot));
        table.AddRow("Admin results", Markup.Escape(plan.LocalResultsJsonPath));
        table.AddRow("CSV results", Markup.Escape(plan.LocalResultsCsvPath));
        return table;
    }

    private static IRenderable CreateResultTargetTable(DispatchRunResult result)
    {
        var table = new Table().RoundedBorder().BorderColor(Color.Grey).Expand();
        table.AddColumn("Target");
        table.AddColumn("State");
        table.AddColumn("Exit");
        table.AddColumn("Failure");
        table.AddColumn("Result");
        foreach (var target in result.Targets)
        {
            table.AddRow(
                Markup.Escape(target.Target),
                FormatTargetState(target.State),
                target.ExitCode?.ToString() ?? "-",
                Markup.Escape(target.FailureCategory == FailureCategory.None
                    ? "-"
                    : $"{target.FailureCategory}: {target.FailureMessage}"),
                Markup.Escape(target.ResultPath));
        }

        return table;
    }

    private static IRenderable CreateResultBreakdown(DispatchRunResult result)
    {
        var chart = new BreakdownChart()
            .Width(60);
        chart.AddItem("Succeeded", Math.Max(result.SuccessCount, 0), Color.Green);
        chart.AddItem("Failed", Math.Max(result.FailedCount, 0), Color.Red);
        chart.AddItem("Timed Out", Math.Max(result.TimedOutCount, 0), Color.Yellow);
        chart.AddItem("Cancelled", Math.Max(result.CancelledCount, 0), Color.Grey);
        return chart;
    }

    private static string FormatDoctorStatus(DispatchDoctorStatus status) =>
        status switch
        {
            DispatchDoctorStatus.Pass => "[green]PASS[/]",
            DispatchDoctorStatus.Warning => "[yellow]WARN[/]",
            DispatchDoctorStatus.Fail => "[red]FAIL[/]",
            _ => Markup.Escape(status.ToString().ToUpperInvariant())
        };

    private static string FormatTargetState(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.Succeeded => "[green]Succeeded[/]",
            TargetExecutionState.Failed => "[red]Failed[/]",
            TargetExecutionState.TimedOut => "[yellow]Timed Out[/]",
            TargetExecutionState.Cancelled => "[grey]Cancelled[/]",
            _ => Markup.Escape(state.ToString())
        };

    private static Color GetResultColor(DispatchRunResult result) =>
        result.FailedCount == 0 && result.TimedOutCount == 0 && result.CancelledCount == 0
            ? Color.Green
            : Color.Red;
}
