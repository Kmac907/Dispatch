using Dispatch.Core;
using Dispatch.Transports.PsExec;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Dispatch.Cli;

internal sealed class SpectreDispatchCommandCenter(
    IAnsiConsole console,
    IDispatchDoctor doctor,
    Func<ConsoleKeyInfo> readKey)
{
    private static readonly string[] MainActions =
    [
        "Start script run",
        "Doctor diagnostics",
        "Command help",
        "Exit"
    ];

    private static readonly string[] TransportChoices =
    [
        PsExecTransportDescriptor.TransportName,
        "psrp",
        "winrm"
    ];

    private readonly CommandCenterState state = new();

    public async Task<CommandCenterResult> RunAsync(CancellationToken cancellationToken)
    {
        return await console
            .Live(Render())
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Bottom)
            .StartAsync(context =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    context.UpdateTarget(Render());
                    context.Refresh();

                    var result = HandleKey(readKey());
                    if (result is not null)
                    {
                        context.UpdateTarget(Render());
                        context.Refresh();
                        return Task.FromResult(result);
                    }
                }

                state.Message = "Cancellation requested.";
                return Task.FromResult(CommandCenterResult.Exit);
            })
            .ConfigureAwait(false);
    }

    internal IRenderable Render() => CreateFrame(state);

    internal CommandCenterResult? HandleKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.F1)
        {
            state.View = CommandCenterView.Help;
            state.Message = "Command reference loaded.";
            return null;
        }

        if (key.Key == ConsoleKey.F5)
        {
            state.DoctorReport = doctor.Run();
            state.View = CommandCenterView.Doctor;
            state.Message = state.DoctorReport.Succeeded
                ? "Doctor checks passed."
                : "Doctor checks need attention.";
            return null;
        }

        return state.View switch
        {
            CommandCenterView.Home => HandleHomeKey(key),
            CommandCenterView.RunSetup => HandleRunSetupKey(key),
            CommandCenterView.Doctor => HandleSimpleViewKey(key),
            CommandCenterView.Help => HandleSimpleViewKey(key),
            _ => null
        };
    }

    private CommandCenterResult? HandleHomeKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
            case ConsoleKey.K:
                state.SelectedAction = Wrap(state.SelectedAction - 1, MainActions.Length);
                state.Message = $"Selected {MainActions[state.SelectedAction]}.";
                return null;
            case ConsoleKey.DownArrow:
            case ConsoleKey.J:
                state.SelectedAction = Wrap(state.SelectedAction + 1, MainActions.Length);
                state.Message = $"Selected {MainActions[state.SelectedAction]}.";
                return null;
            case ConsoleKey.D1:
            case ConsoleKey.NumPad1:
                state.SelectedAction = 0;
                return ActivateHomeSelection();
            case ConsoleKey.D2:
            case ConsoleKey.NumPad2:
                state.SelectedAction = 1;
                return ActivateHomeSelection();
            case ConsoleKey.D3:
            case ConsoleKey.NumPad3:
                state.SelectedAction = 2;
                return ActivateHomeSelection();
            case ConsoleKey.D4:
            case ConsoleKey.NumPad4:
                state.SelectedAction = 3;
                return ActivateHomeSelection();
            case ConsoleKey.Enter:
                return ActivateHomeSelection();
            case ConsoleKey.Escape:
            case ConsoleKey.Q:
                state.Message = "Exiting Dispatch.";
                return CommandCenterResult.Exit;
            default:
                state.Message = "Use arrow keys, number keys, Enter, F1, F5, or Esc.";
                return null;
        }
    }

    private CommandCenterResult? ActivateHomeSelection()
    {
        switch (state.SelectedAction)
        {
            case 0:
                state.View = CommandCenterView.RunSetup;
                state.Message = "Run setup is active. Edit fields, then press Ctrl+R.";
                return null;
            case 1:
                state.DoctorReport = doctor.Run();
                state.View = CommandCenterView.Doctor;
                state.Message = state.DoctorReport.Succeeded
                    ? "Doctor checks passed."
                    : "Doctor checks need attention.";
                return null;
            case 2:
                state.View = CommandCenterView.Help;
                state.Message = "Command reference loaded.";
                return null;
            default:
                state.Message = "Exiting Dispatch.";
                return CommandCenterResult.Exit;
        }
    }

    private CommandCenterResult? HandleSimpleViewKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
            case ConsoleKey.Backspace:
            case ConsoleKey.LeftArrow:
            case ConsoleKey.H:
                state.View = CommandCenterView.Home;
                state.Message = "Returned to the command center.";
                return null;
            case ConsoleKey.Q:
                state.Message = "Exiting Dispatch.";
                return CommandCenterResult.Exit;
            default:
                state.Message = "Press Esc to return, F1 for help, F5 for doctor, or Q to exit.";
                return null;
        }
    }

    private CommandCenterResult? HandleRunSetupKey(ConsoleKeyInfo key)
    {
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.R)
        {
            return TryStartRun();
        }

        switch (key.Key)
        {
            case ConsoleKey.Escape:
                state.View = CommandCenterView.Home;
                state.Message = "Run setup cancelled.";
                return null;
            case ConsoleKey.UpArrow:
                state.SelectedField = Wrap(state.SelectedField - 1, RunSetupField.Count);
                state.Message = $"Editing {RunSetupField.GetLabel(state.SelectedField)}.";
                return null;
            case ConsoleKey.DownArrow:
            case ConsoleKey.Tab:
            case ConsoleKey.Enter:
                state.SelectedField = Wrap(state.SelectedField + 1, RunSetupField.Count);
                state.Message = $"Editing {RunSetupField.GetLabel(state.SelectedField)}.";
                return null;
            case ConsoleKey.LeftArrow:
                AdjustChoice(-1);
                return null;
            case ConsoleKey.RightArrow:
                AdjustChoice(1);
                return null;
            case ConsoleKey.Spacebar:
                ToggleField();
                return null;
            case ConsoleKey.Backspace:
                DeleteCharacter();
                return null;
            default:
                AddCharacter(key.KeyChar);
                return null;
        }
    }

    private CommandCenterResult? TryStartRun()
    {
        if (string.IsNullOrWhiteSpace(state.ScriptPath))
        {
            state.Message = "Script path is required before a run can start.";
            state.SelectedField = RunSetupField.ScriptPath;
            return null;
        }

        if (string.IsNullOrWhiteSpace(state.ComputerNames))
        {
            state.Message = "Computer name or target list is required before a run can start.";
            state.SelectedField = RunSetupField.ComputerNames;
            return null;
        }

        state.Message = state.DryRun
            ? "Starting dry-run planning."
            : "Starting Dispatch execution.";
        return new CommandCenterResult(
            CommandCenterExitKind.StartRun,
            BuildRunArguments());
    }

    private string[] BuildRunArguments()
    {
        var args = new List<string>();
        if (state.DryRun)
        {
            args.Add("--dry-run");
        }

        args.AddRange(
        [
            "--script",
            state.ScriptPath.Trim(),
            "--computer-name",
            state.ComputerNames.Trim(),
            "--transport",
            TransportChoices[state.TransportIndex]
        ]);

        AddOptionalPair(args, "--expected-exit-code", state.ExpectedExitCodes);
        AddOptionalPair(args, "--throttle", state.Throttle);
        if (state.RunAsSystem)
        {
            args.Add("--run-as-system");
        }

        AddOptionalPair(args, "--artifact-path", state.ArtifactPaths);
        AddOptionalPair(args, "--output-root", state.OutputRoot);
        AddOptionalPair(args, "--remote-root", state.RemoteRoot);
        AddScriptArgs(args, state.ScriptArguments);
        return [.. args];
    }

    private static void AddOptionalPair(List<string> args, string option, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            args.AddRange([option, value.Trim()]);
        }
    }

    private static void AddScriptArgs(List<string> args, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        args.Add("--");
        args.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private void AdjustChoice(int direction)
    {
        if (state.SelectedField == RunSetupField.Transport)
        {
            state.TransportIndex = Wrap(state.TransportIndex + direction, TransportChoices.Length);
            state.Message = $"Transport set to {TransportChoices[state.TransportIndex]}.";
            return;
        }

        if (state.SelectedField == RunSetupField.RunAsSystem)
        {
            state.RunAsSystem = !state.RunAsSystem;
            state.Message = $"Run as SYSTEM set to {FormatBoolPlain(state.RunAsSystem)}.";
            return;
        }

        if (state.SelectedField == RunSetupField.DryRun)
        {
            state.DryRun = !state.DryRun;
            state.Message = $"Dry run set to {FormatBoolPlain(state.DryRun)}.";
            return;
        }

        state.Message = "Left and right adjust transport and toggle fields.";
    }

    private void ToggleField()
    {
        if (state.SelectedField is RunSetupField.RunAsSystem or RunSetupField.DryRun)
        {
            AdjustChoice(1);
            return;
        }

        if (state.SelectedField == RunSetupField.Transport)
        {
            AdjustChoice(1);
            return;
        }

        AddCharacter(' ');
    }

    private void AddCharacter(char character)
    {
        if (char.IsControl(character))
        {
            state.Message = "Text fields accept printable characters. Ctrl+R starts the run.";
            return;
        }

        switch (state.SelectedField)
        {
            case RunSetupField.ScriptPath:
                state.ScriptPath += character;
                break;
            case RunSetupField.ComputerNames:
                state.ComputerNames += character;
                break;
            case RunSetupField.Throttle:
                if (!char.IsDigit(character))
                {
                    state.Message = "Throttle accepts digits only.";
                    return;
                }

                state.Throttle += character;
                break;
            case RunSetupField.ExpectedExitCodes:
                if (!char.IsDigit(character) && character != ',')
                {
                    state.Message = "Expected exit codes accept digits and commas.";
                    return;
                }

                state.ExpectedExitCodes += character;
                break;
            case RunSetupField.ArtifactPaths:
                state.ArtifactPaths += character;
                break;
            case RunSetupField.OutputRoot:
                state.OutputRoot += character;
                break;
            case RunSetupField.RemoteRoot:
                state.RemoteRoot += character;
                break;
            case RunSetupField.ScriptArguments:
                state.ScriptArguments += character;
                break;
            default:
                state.Message = "Use arrow keys or Space to change this field.";
                return;
        }

        state.Message = $"Editing {RunSetupField.GetLabel(state.SelectedField)}.";
    }

    private void DeleteCharacter()
    {
        static string TrimLast(string value) =>
            value.Length == 0 ? value : value[..^1];

        switch (state.SelectedField)
        {
            case RunSetupField.ScriptPath:
                state.ScriptPath = TrimLast(state.ScriptPath);
                break;
            case RunSetupField.ComputerNames:
                state.ComputerNames = TrimLast(state.ComputerNames);
                break;
            case RunSetupField.Throttle:
                state.Throttle = TrimLast(state.Throttle);
                break;
            case RunSetupField.ExpectedExitCodes:
                state.ExpectedExitCodes = TrimLast(state.ExpectedExitCodes);
                break;
            case RunSetupField.ArtifactPaths:
                state.ArtifactPaths = TrimLast(state.ArtifactPaths);
                break;
            case RunSetupField.OutputRoot:
                state.OutputRoot = TrimLast(state.OutputRoot);
                break;
            case RunSetupField.RemoteRoot:
                state.RemoteRoot = TrimLast(state.RemoteRoot);
                break;
            case RunSetupField.ScriptArguments:
                state.ScriptArguments = TrimLast(state.ScriptArguments);
                break;
        }

        state.Message = $"Editing {RunSetupField.GetLabel(state.SelectedField)}.";
    }

    private static IRenderable CreateFrame(CommandCenterState state)
    {
        var body = state.View switch
        {
            CommandCenterView.Home => CreateHomeBody(state),
            CommandCenterView.RunSetup => CreateRunSetupBody(state),
            CommandCenterView.Doctor => CreateDoctorBody(state),
            CommandCenterView.Help => CreateHelpBody(),
            _ => CreateHomeBody(state)
        };

        return new Panel(new Rows(
                CreateHeader(state),
                body,
                CreateFooter(state)))
            .Header("[bold steelblue1] Dispatch Live Command Center [/]")
            .RoundedBorder()
            .BorderColor(Color.SteelBlue1)
            .Expand();
    }

    private static IRenderable CreateHeader(CommandCenterState state)
    {
        var grid = new Grid().Expand();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow(
            new Panel(new Markup("[bold]Windows-native script orchestration[/]\n[grey]One operator console for setup, diagnostics, execution, and results.[/]"))
                .Header("[grey] Mission [/]")
                .RoundedBorder()
                .BorderColor(Color.SteelBlue1),
            new Panel(new Markup($"[bold]View[/] {Markup.Escape(GetViewName(state.View))}\n[bold]Version[/] {Markup.Escape(DispatchProduct.Version)}"))
                .Header("[grey] Runtime [/]")
                .RoundedBorder()
                .BorderColor(Color.Grey),
            new Panel(new Markup(Markup.Escape(state.Message)))
                .Header("[grey] Status [/]")
                .RoundedBorder()
                .BorderColor(Color.Yellow));
        return grid;
    }

    private static IRenderable CreateHomeBody(CommandCenterState state)
    {
        var grid = new Grid().Expand();
        grid.AddColumn();
        grid.AddColumn();

        grid.AddRow(CreateMainMenu(state), CreateHomeDashboard(state));
        return grid;
    }

    private static IRenderable CreateMainMenu(CommandCenterState state)
    {
        var table = new Table()
            .RoundedBorder()
            .BorderColor(Color.Grey)
            .Expand();
        table.AddColumn("Key");
        table.AddColumn("Action");
        table.AddColumn("Purpose");

        AddMenuRow(table, state, 0, "1", "Start script run", "Build a Dispatch request and launch dry-run or execution.");
        AddMenuRow(table, state, 1, "2", "Doctor diagnostics", "Run local prerequisite checks in this console.");
        AddMenuRow(table, state, 2, "3", "Command help", "Show command syntax and supported options.");
        AddMenuRow(table, state, 3, "4", "Exit", "Leave the command center.");
        return table;
    }

    private static void AddMenuRow(Table table, CommandCenterState state, int index, string key, string action, string purpose)
    {
        var selected = state.SelectedAction == index;
        table.AddRow(
            selected ? $"[black on steelblue1] {key} [/]" : $"[grey]{key}[/]",
            selected ? $"[black on steelblue1] {Markup.Escape(action)} [/]" : Markup.Escape(action),
            selected ? $"[black on steelblue1] {Markup.Escape(purpose)} [/]" : $"[grey]{Markup.Escape(purpose)}[/]");
    }

    private static IRenderable CreateHomeDashboard(CommandCenterState state)
    {
        var chart = new BarChart()
            .Width(42)
            .Label("[grey]Command Surface[/]")
            .CenterLabel()
            .AddItem("Run", state.SelectedAction == 0 ? 4 : 2, Color.SteelBlue1)
            .AddItem("Doctor", state.SelectedAction == 1 ? 4 : 2, Color.Green)
            .AddItem("Help", state.SelectedAction == 2 ? 4 : 2, Color.Yellow)
            .AddItem("Exit", state.SelectedAction == 3 ? 4 : 2, Color.Grey);

        var breakdown = new BreakdownChart()
            .Width(42)
            .AddItem("Plan", 25, Color.SteelBlue1)
            .AddItem("Execute", 25, Color.Green)
            .AddItem("Diagnose", 25, Color.Yellow)
            .AddItem("Report", 25, Color.Grey);

        return new Panel(new Rows(
                chart,
                new Rule("[grey]Operator Flow[/]").RuleStyle("grey"),
                breakdown))
            .Header("[grey] Command Center [/]")
            .RoundedBorder()
            .BorderColor(Color.Grey);
    }

    private static IRenderable CreateRunSetupBody(CommandCenterState state)
    {
        var grid = new Grid().Expand();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow(CreateRunSetupTable(state), CreateRunSetupSummary(state));
        return grid;
    }

    private static IRenderable CreateRunSetupTable(CommandCenterState state)
    {
        var table = new Table()
            .RoundedBorder()
            .BorderColor(Color.SteelBlue1)
            .Expand();
        table.AddColumn("");
        table.AddColumn("Field");
        table.AddColumn("Value");
        table.AddColumn("Input");

        AddFieldRow(table, state, RunSetupField.ScriptPath, "Script path", state.ScriptPath, "type path");
        AddFieldRow(table, state, RunSetupField.ComputerNames, "Targets", state.ComputerNames, "type names");
        AddFieldRow(table, state, RunSetupField.Transport, "Transport", TransportChoices[state.TransportIndex], "Left/Right/Space");
        AddFieldRow(table, state, RunSetupField.RunAsSystem, "Run as SYSTEM", FormatBool(state.RunAsSystem), "Space");
        AddFieldRow(table, state, RunSetupField.DryRun, "Dry run", FormatBool(state.DryRun), "Space");
        AddFieldRow(table, state, RunSetupField.Throttle, "Throttle", state.Throttle, "digits");
        AddFieldRow(table, state, RunSetupField.ExpectedExitCodes, "Expected exit codes", state.ExpectedExitCodes, "digits/comma");
        AddFieldRow(table, state, RunSetupField.ArtifactPaths, "Artifact paths", state.ArtifactPaths, "optional");
        AddFieldRow(table, state, RunSetupField.OutputRoot, "Output root", state.OutputRoot, "optional");
        AddFieldRow(table, state, RunSetupField.RemoteRoot, "Remote root", state.RemoteRoot, "optional");
        AddFieldRow(table, state, RunSetupField.ScriptArguments, "Script arguments", state.ScriptArguments, "optional");
        return table;
    }

    private static void AddFieldRow(Table table, CommandCenterState state, int field, string label, string value, string input)
    {
        var selected = state.SelectedField == field;
        var marker = selected ? "[steelblue1]>[/]" : "[grey]|[/]";
        var renderedValue = string.IsNullOrEmpty(value) ? "[grey]<empty>[/]" : Markup.Escape(value);
        table.AddRow(
            marker,
            selected ? $"[bold steelblue1]{Markup.Escape(label)}[/]" : Markup.Escape(label),
            selected ? $"[black on steelblue1] {renderedValue} [/]" : renderedValue,
            $"[grey]{Markup.Escape(input)}[/]");
    }

    private static IRenderable CreateRunSetupSummary(CommandCenterState state)
    {
        var ready = !string.IsNullOrWhiteSpace(state.ScriptPath) && !string.IsNullOrWhiteSpace(state.ComputerNames);
        var table = new Table()
            .NoBorder()
            .HideHeaders();
        table.AddColumn("Name");
        table.AddColumn("Value");
        table.AddRow("[grey]Ready[/]", ready ? "[green]yes[/]" : "[yellow]missing required fields[/]");
        table.AddRow("[grey]Mode[/]", state.DryRun ? "[green]dry run[/]" : "[yellow]execute[/]");
        table.AddRow("[grey]Transport[/]", Markup.Escape(TransportChoices[state.TransportIndex]));
        table.AddRow("[grey]Run trigger[/]", "[steelblue1]Ctrl+R[/]");
        table.AddRow("[grey]Back[/]", "[grey]Esc[/]");

        var chart = new BreakdownChart()
            .Width(42)
            .AddItem("Required", ready ? 2 : 1, ready ? Color.Green : Color.Yellow)
            .AddItem("Optional", 8, Color.Grey)
            .AddItem("Controls", 2, Color.SteelBlue1);

        return new Panel(new Rows(
                table,
                new Rule("[grey]Setup Completeness[/]").RuleStyle("grey"),
                chart))
            .Header("[grey] Run Setup [/]")
            .RoundedBorder()
            .BorderColor(ready ? Color.Green : Color.Yellow);
    }

    private static IRenderable CreateDoctorBody(CommandCenterState state)
    {
        if (state.DoctorReport is null)
        {
            return new Panel(new Markup("[grey]Press F5 to run diagnostics.[/]"))
                .Header("[grey] Doctor [/]")
                .RoundedBorder()
                .BorderColor(Color.Grey);
        }

        var table = new Table()
            .RoundedBorder()
            .BorderColor(state.DoctorReport.Succeeded ? Color.Green : Color.Red)
            .Expand();
        table.AddColumn("Status");
        table.AddColumn("Check");
        table.AddColumn("Result");

        foreach (var check in state.DoctorReport.Checks)
        {
            table.AddRow(
                FormatDoctorStatus(check.Status),
                Markup.Escape(check.Name),
                Markup.Escape(string.IsNullOrWhiteSpace(check.Detail)
                    ? check.Message
                    : $"{check.Message} {check.Detail}"));
        }

        return table;
    }

    private static IRenderable CreateHelpBody()
    {
        var table = new Table()
            .RoundedBorder()
            .BorderColor(Color.Grey)
            .Expand();
        table.AddColumn("Command");
        table.AddColumn("Purpose");
        table.AddColumn("Example");
        table.AddRow("dispatch", "Open this live command center.", "dispatch");
        table.AddRow("dispatch run", "Plan or execute a script job.", "dispatch run --script .\\Fix.ps1 --computer-name PC001 --transport psexec");
        table.AddRow("dispatch doctor", "Check local prerequisites.", "dispatch doctor");
        table.AddRow("dispatch --version", "Show installed version.", "dispatch --version");

        return new Panel(new Rows(
                table,
                new Rule("[grey]Automation Note[/]").RuleStyle("grey"),
                new Markup("[grey]Automation should read durable JSON/CSV result files. The terminal is an operator UI.[/]")))
            .Header("[grey] Command Help [/]")
            .RoundedBorder()
            .BorderColor(Color.Grey);
    }

    private static IRenderable CreateFooter(CommandCenterState state)
    {
        var controls = state.View == CommandCenterView.RunSetup
            ? "Up/Down fields | type to edit | Backspace delete | Space/Left/Right change choices | Ctrl+R start | Esc back | F1 help | F5 doctor"
            : "Up/Down select | 1-4 jump | Enter open | F1 help | F5 doctor | Esc/Q exit";

        return new Panel(new Markup($"[grey]{Markup.Escape(controls)}[/]"))
            .Header("[grey] Controls [/]")
            .RoundedBorder()
            .BorderColor(Color.Grey);
    }

    private static string GetViewName(CommandCenterView view) =>
        view switch
        {
            CommandCenterView.Home => "Home",
            CommandCenterView.RunSetup => "Run Setup",
            CommandCenterView.Doctor => "Doctor",
            CommandCenterView.Help => "Help",
            _ => "Home"
        };

    private static string FormatDoctorStatus(DispatchDoctorStatus status) =>
        status switch
        {
            DispatchDoctorStatus.Pass => "[green]PASS[/]",
            DispatchDoctorStatus.Warning => "[yellow]WARN[/]",
            DispatchDoctorStatus.Fail => "[red]FAIL[/]",
            _ => Markup.Escape(status.ToString().ToUpperInvariant())
        };

    private static string FormatBool(bool value) =>
        value ? "[green]yes[/]" : "[grey]no[/]";

    private static string FormatBoolPlain(bool value) =>
        value ? "yes" : "no";

    private static int Wrap(int value, int count) =>
        (value % count + count) % count;

    private sealed class CommandCenterState
    {
        public CommandCenterView View { get; set; }

        public int SelectedAction { get; set; }

        public int SelectedField { get; set; }

        public string Message { get; set; } = "Ready. Select an action.";

        public DispatchDoctorReport? DoctorReport { get; set; }

        public string ScriptPath { get; set; } = string.Empty;

        public string ComputerNames { get; set; } = string.Empty;

        public int TransportIndex { get; set; }

        public bool RunAsSystem { get; set; }

        public bool DryRun { get; set; } = true;

        public string Throttle { get; set; } = string.Empty;

        public string ExpectedExitCodes { get; set; } = "0";

        public string ArtifactPaths { get; set; } = string.Empty;

        public string OutputRoot { get; set; } = string.Empty;

        public string RemoteRoot { get; set; } = string.Empty;

        public string ScriptArguments { get; set; } = string.Empty;
    }
}

internal sealed record CommandCenterResult(CommandCenterExitKind Kind, string[] RunArguments)
{
    public static CommandCenterResult Exit { get; } = new(CommandCenterExitKind.Exit, []);
}

internal enum CommandCenterExitKind
{
    Exit,
    StartRun
}

internal enum CommandCenterView
{
    Home,
    RunSetup,
    Doctor,
    Help
}

internal static class RunSetupField
{
    public const int ScriptPath = 0;
    public const int ComputerNames = 1;
    public const int Transport = 2;
    public const int RunAsSystem = 3;
    public const int DryRun = 4;
    public const int Throttle = 5;
    public const int ExpectedExitCodes = 6;
    public const int ArtifactPaths = 7;
    public const int OutputRoot = 8;
    public const int RemoteRoot = 9;
    public const int ScriptArguments = 10;
    public const int Count = 11;

    public static string GetLabel(int field) =>
        field switch
        {
            ScriptPath => "Script path",
            ComputerNames => "Targets",
            Transport => "Transport",
            RunAsSystem => "Run as SYSTEM",
            DryRun => "Dry run",
            Throttle => "Throttle",
            ExpectedExitCodes => "Expected exit codes",
            ArtifactPaths => "Artifact paths",
            OutputRoot => "Output root",
            RemoteRoot => "Remote root",
            ScriptArguments => "Script arguments",
            _ => "Field"
        };
}
