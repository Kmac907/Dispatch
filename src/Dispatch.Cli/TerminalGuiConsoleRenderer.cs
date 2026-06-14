using System.Text;
using Dispatch.Core;
using Dispatch.Core.Models;

namespace Dispatch.Cli;

internal static class TerminalGuiConsoleRenderer
{
    public static void RenderHome(TextWriter writer)
    {
        WriteShell(
            writer,
            "Dispatch Command Center",
            [
                "Mode: Terminal.Gui command service",
                $"Version: {DispatchProduct.Version}",
                string.Empty,
                "Commands",
                "  dispatch run      Plan or execute a script job",
                "  dispatch doctor   Check local prerequisites",
                "  dispatch --version Show installed version",
                string.Empty,
                "Interactive terminal: run dispatch with no arguments to open the retained command center."
            ]);
    }

    public static void RenderVersion(TextWriter writer)
    {
        WriteShell(
            writer,
            "Dispatch Version",
            [
                "Product: Dispatch",
                $"Version: {DispatchProduct.Version}",
                "Mode: Terminal.Gui command service"
            ]);
    }

    public static void RenderRootHelp(TextWriter writer) => RenderHome(writer);

    public static void RenderRunHelp(TextWriter writer)
    {
        WriteShell(
            writer,
            "Run Command",
            [
                "Build and execute a Dispatch job through the shared planner/executor path.",
                string.Empty,
                "Options",
                "  --script              PowerShell script path",
                "  --computer-name       Target name or comma-separated targets",
                "  --target-file         Target file path",
                "  --transport           Transport: psexec, psrp, or winrm",
                "  --dry-run             Render the execution plan without endpoint work",
                "  --no-dashboard        Use compact Terminal.Gui progress instead of the full dashboard",
                "  --expected-exit-code  Expected code or comma-separated codes",
                "  --throttle            Maximum concurrent target executions",
                "  --artifact-path       Relative artifact path or comma-separated paths",
                string.Empty,
                @"Example: dispatch run --dry-run --script .\Fix.ps1 --computer-name PC001 --transport psexec"
            ]);
    }

    public static void RenderDoctorHelp(TextWriter writer)
    {
        WriteShell(
            writer,
            "Doctor Command",
            [
                "Validate local prerequisites before endpoint execution.",
                "Checks Windows host support, PowerShell availability, PsExec path resolution,",
                "output-root writability, and admin context."
            ]);
    }

    public static void RenderError(TextWriter writer, string title, string message)
    {
        WriteShell(
            writer,
            title,
            [
                $"Error: {message}",
                "Next: run a help command to review valid options."
            ],
            ShellTone.Error);
    }

    public static void RenderDryRunProgressSummary(TextWriter writer)
    {
        WriteShell(
            writer,
            "Dry Run Progress",
            [
                "PASS  Validate dry-run request   [####################] 100%",
                "PASS  Build execution plan       [####################] 100%",
                "PASS  Resolve target layout      [####################] 100%",
                "PASS  Prepare dry-run view       [####################] 100%"
            ]);
    }

    public static void RenderPlanningStatus(TextWriter writer)
    {
        WriteShell(
            writer,
            "Planning Status",
            [
                "ACTIVE Build Dispatch execution plan",
                "Target resolution, local run layout, and transport validation are using the shared planner."
            ]);
    }

    public static void RenderDryRunPlan(TextWriter writer, ExecutionPlan plan)
    {
        var lines = new List<string>
        {
            "Execution Plan",
            $"Run ID: {plan.RunId}",
            $"Transport: {plan.Job.Transport}",
            $"Payload: {plan.Job.Payload.DisplayName}",
            $"Targets: {plan.Targets.Count}",
            $"Throttle: {plan.ThrottleLimit}",
            string.Empty,
            "Targets"
        };

        foreach (var target in plan.Targets)
        {
            lines.Add($"  {target.Target.Name} | {target.State} | {target.PlannedRemoteScriptPath ?? "Pending"}");
        }

        lines.Add(string.Empty);
        lines.Add($"Local run root: {plan.LocalRunRoot}");
        lines.Add($"Admin results: {plan.LocalResultsJsonPath}");
        lines.Add($"CSV results: {plan.LocalResultsCsvPath}");

        WriteShell(writer, "Dispatch Dry Run", lines);
    }

    public static void RenderRunResult(TextWriter writer, DispatchRunResult result)
    {
        var succeeded = result.FailedCount == 0 && result.TimedOutCount == 0 && result.CancelledCount == 0;
        var lines = new List<string>
        {
            $"Run ID: {result.RunId}",
            $"Transport: {result.Transport}",
            $"Targets: {result.TargetCount}",
            $"Succeeded: {result.SuccessCount}",
            $"Failed: {result.FailedCount}",
            $"Timed Out: {result.TimedOutCount}",
            $"Cancelled: {result.CancelledCount}",
            string.Empty,
            "Target Results"
        };

        foreach (var target in result.Targets)
        {
            var failure = target.FailureCategory == FailureCategory.None
                ? "-"
                : $"{target.FailureCategory}: {target.FailureMessage}";
            lines.Add($"  {FormatStatusSymbol(target.State)} {target.Target} | {target.State} | exit {target.ExitCode?.ToString() ?? "-"} | {failure}");
        }

        lines.Add(string.Empty);
        lines.Add($"Result file: {result.ResultPath}");

        WriteShell(
            writer,
            succeeded ? "Dispatch Run Complete" : "Dispatch Run Completed With Failures",
            lines,
            succeeded ? ShellTone.Success : ShellTone.Error);
    }

    public static void RenderDoctorReport(TextWriter writer, DispatchDoctorReport report)
    {
        var lines = report.Checks
            .Select(check => $"{FormatDoctorStatus(check.Status),-4}  {check.Name} | {(string.IsNullOrWhiteSpace(check.Detail) ? check.Message : $"{check.Message} {check.Detail}")}")
            .ToArray();

        WriteShell(
            writer,
            report.Succeeded ? "Dispatch Doctor Passed" : "Dispatch Doctor Failed",
            lines,
            report.Succeeded ? ShellTone.Success : ShellTone.Error);
    }

    public static string BuildShellSnapshot(string title, IEnumerable<string> lines, ShellTone tone = ShellTone.Standard)
    {
        var content = lines.ToArray();
        var heading = $" Dispatch Terminal UI :: {title}";
        var width = Math.Max(
            heading.Length + 4,
            content.Length == 0 ? 40 : content.Max(static line => line.Length) + 4);
        width = Math.Min(Math.Max(width, 48), 140);

        var border = tone switch
        {
            ShellTone.Success => "╔" + new string('═', width - 2) + "╗",
            ShellTone.Error => "╔" + new string('═', width - 2) + "╗",
            _ => "┌" + new string('─', width - 2) + "┐"
        };
        var bottom = tone switch
        {
            ShellTone.Success => "╚" + new string('═', width - 2) + "╝",
            ShellTone.Error => "╚" + new string('═', width - 2) + "╝",
            _ => "└" + new string('─', width - 2) + "┘"
        };

        var builder = new StringBuilder();
        builder.AppendLine(border);
        builder.AppendLine(PadLine(heading, width));
        builder.AppendLine(PadLine(new string('─', Math.Min(width - 4, Math.Max(8, title.Length + 24))), width));
        foreach (var line in content)
        {
            builder.AppendLine(PadLine(line, width));
        }

        builder.AppendLine(bottom);
        return builder.ToString();
    }

    private static void WriteShell(TextWriter writer, string title, IEnumerable<string> lines, ShellTone tone = ShellTone.Standard) =>
        writer.Write(BuildShellSnapshot(title, lines, tone));

    private static string PadLine(string value, int width)
    {
        var visible = value.Length > width - 4
            ? string.Concat(value.AsSpan(0, width - 7), "...")
            : value;
        return $"│ {visible.PadRight(width - 4)} │";
    }

    internal static string FormatStatusSymbol(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.Succeeded => "✓",
            TargetExecutionState.Failed => "×",
            TargetExecutionState.TimedOut => "!",
            TargetExecutionState.Cancelled => "-",
            TargetExecutionState.Pending => "○",
            _ => "●"
        };

    private static string FormatDoctorStatus(DispatchDoctorStatus status) =>
        status switch
        {
            DispatchDoctorStatus.Pass => "PASS",
            DispatchDoctorStatus.Warning => "WARN",
            DispatchDoctorStatus.Fail => "FAIL",
            _ => status.ToString().ToUpperInvariant()
        };
}

internal enum ShellTone
{
    Standard,
    Success,
    Error
}
