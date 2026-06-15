using Dispatch.Core;
using Dispatch.Transports.PsExec;
using Terminal.Gui;

namespace Dispatch.Cli;

internal sealed class TerminalGuiDispatchCommandCenter(
    IDispatchDoctor doctor)
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
    private CommandCenterResult? pendingResult;

    public Task<CommandCenterResult> RunAsync(CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Out.Write(RenderSnapshot());
            return Task.FromResult(CommandCenterResult.Exit);
        }

        Application.Init();
        try
        {
            ApplyColorSchemes();
            var session = new TerminalGuiCommandCenterSession(doctor);
            return Task.FromResult(session.Run(cancellationToken));
        }
        finally
        {
            Application.Shutdown();
        }
    }

    internal string RenderSnapshot()
    {
        var lines = state.View switch
        {
            CommandCenterView.Home => RenderHomeSnapshot(),
            CommandCenterView.RunSetup => RenderRunSetupSnapshot(),
            CommandCenterView.Doctor => RenderDoctorSnapshot(),
            CommandCenterView.Help => RenderHelpSnapshot(),
            _ => RenderHomeSnapshot()
        };

        return TerminalGuiConsoleRenderer.BuildShellSnapshot("Dispatch Live Command Center", lines);
    }

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

    private static void ApplyColorSchemes()
    {
        Colors.Base.Normal = Application.Driver.MakeAttribute(Color.White, Color.Black);
        Colors.Base.Focus = Application.Driver.MakeAttribute(Color.Black, Color.Cyan);
        Colors.Base.HotNormal = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black);
        Colors.Base.HotFocus = Application.Driver.MakeAttribute(Color.Black, Color.BrightCyan);
        Colors.Dialog.Normal = Application.Driver.MakeAttribute(Color.White, Color.Blue);
        Colors.Menu.Normal = Application.Driver.MakeAttribute(Color.White, Color.Blue);
        Colors.Menu.Focus = Application.Driver.MakeAttribute(Color.Black, Color.Cyan);
    }

    private Window BuildRootView()
    {
        var root = new Window("Dispatch Command Center")
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            CanFocus = true
        };

        void Refresh()
        {
            RefreshRoot(root);
            Application.Refresh();
        }

        var menu = new MenuBar(new[]
        {
            new MenuBarItem("_Dispatch", new[]
            {
                new MenuItem("_Run", "Open run setup", () =>
                {
                    state.View = CommandCenterView.RunSetup;
                    state.Message = "Run setup is active. Edit fields, then press Ctrl+R.";
                    Refresh();
                }),
                new MenuItem("_Doctor", "Run diagnostics", () =>
                {
                    state.DoctorReport = doctor.Run();
                    state.View = CommandCenterView.Doctor;
                    state.Message = state.DoctorReport.Succeeded
                        ? "Doctor checks passed."
                        : "Doctor checks need attention.";
                    Refresh();
                }),
                new MenuItem("_Help", "Show command help", () =>
                {
                    state.View = CommandCenterView.Help;
                    state.Message = "Command reference loaded.";
                    Refresh();
                }),
                new MenuItem("_Quit", "Exit Dispatch", () =>
                {
                    state.Message = "Exiting Dispatch.";
                    Application.RequestStop();
                })
            })
        });
        Application.Top.Add(menu);

        var status = new StatusBar(new[]
        {
            new StatusItem(Key.F1, "~F1~ Help", () =>
            {
                state.View = CommandCenterView.Help;
                state.Message = "Command reference loaded.";
                Refresh();
            }),
            new StatusItem(Key.F5, "~F5~ Doctor", () =>
            {
                state.DoctorReport = doctor.Run();
                state.View = CommandCenterView.Doctor;
                state.Message = state.DoctorReport.Succeeded
                    ? "Doctor checks passed."
                    : "Doctor checks need attention.";
                Refresh();
            }),
            new StatusItem(Key.CtrlMask | Key.R, "~Ctrl+R~ Run", () =>
            {
                StartRunIfReady();
                Refresh();
            }),
            new StatusItem(Key.Esc, "~Esc~ Back", () =>
            {
                state.View = CommandCenterView.Home;
                Refresh();
            })
        });
        Application.Top.Add(status);

        RefreshRoot(root);
        root.SetFocus();
        return root;
    }

    private void RefreshRoot(View root)
    {
        root.RemoveAll();
        root.Add(CreateHeader());
        root.Add(state.View switch
        {
            CommandCenterView.Home => CreateHomeView(),
            CommandCenterView.RunSetup => CreateRunSetupView(),
            CommandCenterView.Doctor => CreateDoctorView(),
            CommandCenterView.Help => CreateHelpView(),
            _ => CreateHomeView()
        });
        root.Add(CreateFooter());
    }

    private View CreateHeader()
    {
        var frame = new FrameView("Status")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 5
        };
        frame.Add(new Label($"View: {GetViewName(state.View)}")
        {
            X = 1,
            Y = 0
        });
        frame.Add(new Label($"Version: {DispatchProduct.Version}")
        {
            X = 30,
            Y = 0
        });
        frame.Add(new Label(state.Message)
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(2)
        });
        return frame;
    }

    private View CreateHomeView()
    {
        var menuFrame = new FrameView("Menu")
        {
            X = 0,
            Y = 5,
            Width = Dim.Percent(45),
            Height = Dim.Fill(4)
        };
        var list = new ListView(MainActions)
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2),
            SelectedItem = state.SelectedAction
        };
        menuFrame.Add(list);

        var dashboard = new FrameView("Command Surface")
        {
            X = Pos.Right(menuFrame),
            Y = 5,
            Width = Dim.Fill(),
            Height = Dim.Fill(4)
        };
        dashboard.Add(new Label("Windows-native script orchestration")
        {
            X = 1,
            Y = 1
        });
        dashboard.Add(new Label("Plan  [##########]  Execute [##########]")
        {
            X = 1,
            Y = 3
        });
        dashboard.Add(new Label("Diagnose [##########] Report  [##########]")
        {
            X = 1,
            Y = 4
        });
        dashboard.Add(new Label("Use arrow keys, number keys, Enter, F1, F5, or Esc.")
        {
            X = 1,
            Y = 6
        });

        return new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        }
        .AddChained(menuFrame, dashboard);
    }

    private View CreateRunSetupView()
    {
        var form = new FrameView("Run Setup")
        {
            X = 0,
            Y = 5,
            Width = Dim.Percent(70),
            Height = Dim.Fill(4)
        };

        AddTextField(form, RunSetupField.ScriptPath, "Script path", state.ScriptPath, 0);
        AddTextField(form, RunSetupField.ComputerNames, "Targets", state.ComputerNames, 2);
        form.Add(new Label($"{SelectionMarker(RunSetupField.Transport)} Transport")
        {
            X = 1,
            Y = 4
        });
        form.Add(new ListView(TransportChoices)
        {
            X = 24,
            Y = 4,
            Width = 18,
            Height = 3,
            SelectedItem = state.TransportIndex
        });
        form.Add(new CheckBox("Run as SYSTEM", state.RunAsSystem)
        {
            X = 1,
            Y = 8
        });
        form.Add(new CheckBox("Dry run", state.DryRun)
        {
            X = 24,
            Y = 8
        });
        AddTextField(form, RunSetupField.Throttle, "Throttle", state.Throttle, 10);
        AddTextField(form, RunSetupField.ExpectedExitCodes, "Expected exits", state.ExpectedExitCodes, 12);
        AddTextField(form, RunSetupField.ArtifactPaths, "Artifact paths", state.ArtifactPaths, 14);
        AddTextField(form, RunSetupField.OutputRoot, "Output root", state.OutputRoot, 16);
        AddTextField(form, RunSetupField.RemoteRoot, "Remote root", state.RemoteRoot, 18);
        AddTextField(form, RunSetupField.ScriptArguments, "Script args", state.ScriptArguments, 20);

        var summary = new FrameView("Launch")
        {
            X = Pos.Right(form),
            Y = 5,
            Width = Dim.Fill(),
            Height = Dim.Fill(4)
        };
        var ready = !string.IsNullOrWhiteSpace(state.ScriptPath) && !string.IsNullOrWhiteSpace(state.ComputerNames);
        var progress = new ProgressBar
        {
            X = 1,
            Y = 6,
            Width = Dim.Fill(2),
            Fraction = ready ? 1 : 0.5f
        };
        summary.Add(new Label($"Ready: {(ready ? "yes" : "missing required fields")}") { X = 1, Y = 1 });
        summary.Add(new Label($"Mode: {(state.DryRun ? "dry run" : "execute")}") { X = 1, Y = 2 });
        summary.Add(new Label($"Transport: {TransportChoices[state.TransportIndex]}") { X = 1, Y = 3 });
        summary.Add(new Label("Ctrl+R launches the shared run path.") { X = 1, Y = 5 });
        summary.Add(progress);
        var launch = new Button("Launch")
        {
            X = 1,
            Y = 8
        };
        launch.Clicked += StartRunIfReady;
        summary.Add(launch);

        var back = new Button("Back")
        {
            X = 14,
            Y = 8
        };
        back.Clicked += () =>
        {
            state.View = CommandCenterView.Home;
            state.Message = "Returned to the command center.";
        };
        summary.Add(back);

        return new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        }
        .AddChained(form, summary);
    }

    private void AddTextField(View parent, int field, string label, string value, int y)
    {
        parent.Add(new Label($"{SelectionMarker(field)} {label}")
        {
            X = 1,
            Y = y
        });
        parent.Add(new TextField(value)
        {
            X = 24,
            Y = y,
            Width = Dim.Fill(2)
        });
    }

    private string SelectionMarker(int field) =>
        state.SelectedField == field ? ">" : " ";

    private View CreateDoctorView()
    {
        var frame = new FrameView("Doctor")
        {
            X = 0,
            Y = 5,
            Width = Dim.Fill(),
            Height = Dim.Fill(4)
        };

        if (state.DoctorReport is null)
        {
            frame.Add(new Label("Press F5 to run diagnostics.") { X = 1, Y = 1 });
            return frame;
        }

        var y = 1;
        foreach (var check in state.DoctorReport.Checks)
        {
            frame.Add(new Label($"{FormatDoctorStatus(check.Status),-4} {check.Name,-24} {check.Message} {check.Detail}".Trim())
            {
                X = 1,
                Y = y++,
                Width = Dim.Fill(2)
            });
        }

        return frame;
    }

    private static View CreateHelpView()
    {
        var frame = new FrameView("Command Help")
        {
            X = 0,
            Y = 5,
            Width = Dim.Fill(),
            Height = Dim.Fill(4)
        };

        var lines = new[]
        {
            "dispatch                         Open the retained Terminal.Gui command center",
            @"dispatch run --script .\Fix.ps1 --computer-name PC001 --transport psexec",
            "dispatch doctor                  Check local prerequisites",
            "dispatch --version               Show installed version",
            string.Empty,
            "Automation should read durable JSON/CSV result files. The terminal is operator UI."
        };

        for (var index = 0; index < lines.Length; index++)
        {
            frame.Add(new Label(lines[index]) { X = 1, Y = index + 1, Width = Dim.Fill(2) });
        }

        return frame;
    }

    private View CreateFooter()
    {
        var controls = state.View == CommandCenterView.RunSetup
            ? "Up/Down fields | type edit | Backspace delete | Space/Left/Right choices | Ctrl+R start | Esc back | F1 help | F5 doctor"
            : "Up/Down select | 1-4 jump | Enter open | F1 help | F5 doctor | Esc/Q exit";

        var frame = new FrameView("Controls")
        {
            X = 0,
            Y = Pos.AnchorEnd(4),
            Width = Dim.Fill(),
            Height = 4
        };
        frame.Add(new Label(controls) { X = 1, Y = 0, Width = Dim.Fill(2) });
        return frame;
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

    private void StartRunIfReady()
    {
        var result = TryStartRun();
        if (result is null)
        {
            return;
        }

        pendingResult = result;
        Application.RequestStop();
    }

    private string[] BuildRunArguments()
    {
        return BuildRunArgumentsFromValues(
            state.DryRun,
            state.ScriptPath,
            state.ComputerNames,
            state.TransportIndex,
            state.RunAsSystem,
            state.ExpectedExitCodes,
            state.Throttle,
            state.ArtifactPaths,
            state.OutputRoot,
            state.RemoteRoot,
            state.ScriptArguments);
    }

    internal static string[] BuildRunArgumentsFromValues(
        bool dryRun,
        string scriptPath,
        string computerNames,
        int transportIndex,
        bool runAsSystem,
        string expectedExitCodes,
        string throttle,
        string artifactPaths,
        string outputRoot,
        string remoteRoot,
        string scriptArguments)
    {
        var args = new List<string>();
        if (dryRun)
        {
            args.Add("--dry-run");
        }

        args.AddRange(
        [
            "--script",
            scriptPath.Trim(),
            "--computer-name",
            computerNames.Trim(),
            "--transport",
            TransportChoices[Wrap(transportIndex, TransportChoices.Length)]
        ]);

        AddOptionalPair(args, "--expected-exit-code", expectedExitCodes);
        AddOptionalPair(args, "--throttle", throttle);
        if (runAsSystem)
        {
            args.Add("--run-as-system");
        }

        AddOptionalPair(args, "--artifact-path", artifactPaths);
        AddOptionalPair(args, "--output-root", outputRoot);
        AddOptionalPair(args, "--remote-root", remoteRoot);
        AddScriptArgs(args, scriptArguments);
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

    private IEnumerable<string> RenderHomeSnapshot()
    {
        yield return $"View: {GetViewName(state.View)}";
        yield return $"Status: {state.Message}";
        yield return string.Empty;
        for (var index = 0; index < MainActions.Length; index++)
        {
            yield return $"{(state.SelectedAction == index ? ">" : " ")} {index + 1}. {MainActions[index]}";
        }

        yield return string.Empty;
        yield return "F1 help | F5 doctor | Enter open | Esc/Q exit";
    }

    private IEnumerable<string> RenderRunSetupSnapshot()
    {
        yield return $"View: {GetViewName(state.View)}";
        yield return $"Status: {state.Message}";
        yield return string.Empty;
        foreach (var row in GetRunSetupRows())
        {
            yield return $"{(row.Selected ? ">" : " ")} {row.Label,-20} {row.Value}";
        }

        yield return string.Empty;
        yield return "Ctrl+R start | Space/Left/Right choices | Esc back";
    }

    private IEnumerable<string> RenderDoctorSnapshot()
    {
        yield return $"View: {GetViewName(state.View)}";
        yield return $"Status: {state.Message}";
        yield return string.Empty;
        if (state.DoctorReport is null)
        {
            yield return "Press F5 to run diagnostics.";
            yield break;
        }

        foreach (var check in state.DoctorReport.Checks)
        {
            yield return $"{FormatDoctorStatus(check.Status),-4} {check.Name,-24} {check.Message} {check.Detail}".Trim();
        }
    }

    private static IEnumerable<string> RenderHelpSnapshot()
    {
        yield return "dispatch                         Open the retained Terminal.Gui command center";
        yield return @"dispatch run --script .\Fix.ps1 --computer-name PC001 --transport psexec";
        yield return "dispatch doctor                  Check local prerequisites";
        yield return "dispatch --version               Show installed version";
        yield return string.Empty;
        yield return "Automation should read durable JSON/CSV result files.";
    }

    private IEnumerable<(bool Selected, string Label, string Value)> GetRunSetupRows()
    {
        yield return (state.SelectedField == RunSetupField.ScriptPath, "Script path", EmptyText(state.ScriptPath));
        yield return (state.SelectedField == RunSetupField.ComputerNames, "Targets", EmptyText(state.ComputerNames));
        yield return (state.SelectedField == RunSetupField.Transport, "Transport", TransportChoices[state.TransportIndex]);
        yield return (state.SelectedField == RunSetupField.RunAsSystem, "Run as SYSTEM", FormatBoolPlain(state.RunAsSystem));
        yield return (state.SelectedField == RunSetupField.DryRun, "Dry run", FormatBoolPlain(state.DryRun));
        yield return (state.SelectedField == RunSetupField.Throttle, "Throttle", EmptyText(state.Throttle));
        yield return (state.SelectedField == RunSetupField.ExpectedExitCodes, "Expected exit codes", EmptyText(state.ExpectedExitCodes));
        yield return (state.SelectedField == RunSetupField.ArtifactPaths, "Artifact paths", EmptyText(state.ArtifactPaths));
        yield return (state.SelectedField == RunSetupField.OutputRoot, "Output root", EmptyText(state.OutputRoot));
        yield return (state.SelectedField == RunSetupField.RemoteRoot, "Remote root", EmptyText(state.RemoteRoot));
        yield return (state.SelectedField == RunSetupField.ScriptArguments, "Script arguments", EmptyText(state.ScriptArguments));
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
            DispatchDoctorStatus.Pass => "PASS",
            DispatchDoctorStatus.Warning => "WARN",
            DispatchDoctorStatus.Fail => "FAIL",
            _ => status.ToString().ToUpperInvariant()
        };

    private static string FormatBoolPlain(bool value) =>
        value ? "yes" : "no";

    private static string EmptyText(string value) =>
        string.IsNullOrEmpty(value) ? "<empty>" : value;

    private static int Wrap(int value, int count) =>
        (value % count + count) % count;

    private static ConsoleKeyInfo ToConsoleKeyInfo(KeyEvent keyEvent)
    {
        var character = keyEvent.KeyValue is >= 32 and <= char.MaxValue
            ? (char)keyEvent.KeyValue
            : '\0';
        var key = keyEvent.Key switch
        {
            Key.CursorUp => ConsoleKey.UpArrow,
            Key.CursorDown => ConsoleKey.DownArrow,
            Key.CursorLeft => ConsoleKey.LeftArrow,
            Key.CursorRight => ConsoleKey.RightArrow,
            Key.Backspace => ConsoleKey.Backspace,
            Key.Tab => ConsoleKey.Tab,
            Key.Enter => ConsoleKey.Enter,
            Key.Esc => ConsoleKey.Escape,
            Key.Space => ConsoleKey.Spacebar,
            Key.F1 => ConsoleKey.F1,
            Key.F5 => ConsoleKey.F5,
            Key.D1 => ConsoleKey.D1,
            Key.D2 => ConsoleKey.D2,
            Key.D3 => ConsoleKey.D3,
            Key.D4 => ConsoleKey.D4,
            _ => GetConsoleKey(character)
        };

        return new ConsoleKeyInfo(character, key, keyEvent.IsShift, keyEvent.IsAlt, keyEvent.IsCtrl);
    }

    private static ConsoleKey GetConsoleKey(char character)
    {
        if (char.IsLetter(character))
        {
            return Enum.Parse<ConsoleKey>(character.ToString().ToUpperInvariant());
        }

        if (char.IsDigit(character))
        {
            return Enum.Parse<ConsoleKey>($"D{character}");
        }

        return character switch
        {
            ':' => ConsoleKey.Oem1,
            '\\' => ConsoleKey.Oem5,
            '.' => ConsoleKey.OemPeriod,
            '-' => ConsoleKey.OemMinus,
            '_' => ConsoleKey.OemMinus,
            ' ' => ConsoleKey.Spacebar,
            _ => ConsoleKey.NoName
        };
    }

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

    private sealed class TerminalGuiCommandCenterSession(IDispatchDoctor doctor)
    {
        private readonly StatusModel status = new();
        private CommandCenterResult result = CommandCenterResult.Exit;
        private View? mainPane;
        private Label? viewLabel;
        private Label? messageLabel;
        private Label? readinessLabel;
        private ProgressBar? readinessBar;
        private TextField? scriptPathField;
        private TextField? targetsField;
        private ListView? transportList;
        private CheckBox? runAsSystemBox;
        private CheckBox? dryRunBox;
        private TextField? throttleField;
        private TextField? expectedExitField;
        private TextField? artifactField;
        private TextField? outputRootField;
        private TextField? remoteRootField;
        private TextField? scriptArgumentsField;

        public CommandCenterResult Run(CancellationToken cancellationToken)
        {
            var top = Application.Top;
            top.RemoveAll();
            BuildShell(top);
            ShowHome();
            using var cancellationRegistration = cancellationToken.Register(static () => Application.RequestStop());
            Application.Run();
            return result;
        }

        private void BuildShell(Toplevel top)
        {
            top.Add(new MenuBar(new[]
            {
                new MenuBarItem("_Dispatch", new[]
                {
                    new MenuItem("_Home", "Open command center home", ShowHome),
                    new MenuItem("_Run", "Open script run setup", ShowRunSetup),
                    new MenuItem("_Doctor", "Run local diagnostics", ShowDoctor),
                    new MenuItem("_Help", "Show command help", ShowHelp),
                    new MenuItem("_Quit", "Exit Dispatch", () =>
                    {
                        result = CommandCenterResult.Exit;
                        Application.RequestStop();
                    })
                })
            }));

            var shell = new Window("Dispatch Command Center")
            {
                X = 0,
                Y = 1,
                Width = Dim.Fill(),
                Height = Dim.Fill(1)
            };

            var header = new FrameView("Command Service")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = 6
            };
            viewLabel = new Label("Home") { X = 1, Y = 0, Width = Dim.Percent(35) };
            header.Add(viewLabel);
            header.Add(new Label($"Version: {DispatchProduct.Version}") { X = Pos.Percent(35), Y = 0, Width = Dim.Percent(30) });
            header.Add(new Label("Renderer: Terminal.Gui retained TUI") { X = Pos.Percent(65), Y = 0, Width = Dim.Fill(2) });
            messageLabel = new Label("Ready. Select an action.") { X = 1, Y = 2, Width = Dim.Fill(2) };
            header.Add(messageLabel);

            var nav = new FrameView("Navigation")
            {
                X = 0,
                Y = 6,
                Width = 28,
                Height = Dim.Fill()
            };
            AddNavigationButton(nav, "Start Run", 1, ShowRunSetup);
            AddNavigationButton(nav, "Doctor", 4, ShowDoctor);
            AddNavigationButton(nav, "Help", 7, ShowHelp);
            AddNavigationButton(nav, "Exit", 10, () =>
            {
                result = CommandCenterResult.Exit;
                Application.RequestStop();
            });

            mainPane = new FrameView("Workspace")
            {
                X = Pos.Right(nav),
                Y = 6,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };

            shell.Add(header, nav, mainPane);
            top.Add(shell);
            top.Add(new StatusBar(new[]
            {
                new StatusItem(Key.F1, "~F1~ Help", ShowHelp),
                new StatusItem(Key.F5, "~F5~ Doctor", ShowDoctor),
                new StatusItem(Key.CtrlMask | Key.R, "~Ctrl+R~ Launch", LaunchRunFromControls),
                new StatusItem(Key.Esc, "~Esc~ Home", ShowHome)
            }));
        }

        private static void AddNavigationButton(View parent, string text, int y, Action clicked)
        {
            var button = new Button(text)
            {
                X = 1,
                Y = y,
                Width = Dim.Fill(2)
            };
            button.Clicked += clicked;
            parent.Add(button);
        }

        private void ShowHome()
        {
            SetStatus("Home", "Ready. Choose Start Run for a script job, Doctor for local checks, or Help for command reference.");
            ResetWorkspace("Overview");
            var pane = RequireMainPane();
            pane.Add(new Label("Windows-native script orchestration for endpoint engineering.")
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(2)
            });
            pane.Add(new Label("Operator flow") { X = 1, Y = 3 });
            AddPhase(pane, "Plan", 5, 0.35f);
            AddPhase(pane, "Prepare", 7, 0.45f);
            AddPhase(pane, "Execute", 9, 0.65f);
            AddPhase(pane, "Report", 11, 0.85f);
            pane.SetFocus();
            Application.Refresh();
        }

        private static void AddPhase(View parent, string label, int y, float value)
        {
            parent.Add(new Label(label) { X = 1, Y = y, Width = 14 });
            parent.Add(new ProgressBar
            {
                X = 16,
                Y = y,
                Width = Dim.Fill(2),
                Fraction = value
            });
        }

        private void ShowRunSetup()
        {
            SetStatus("Run Setup", "Edit fields directly. Values stay in the controls until you launch or exit.");
            ResetWorkspace("Run Setup");
            EnsureRunControls();

            var pane = RequireMainPane();
            var form = new FrameView("Script Job")
            {
                X = 0,
                Y = 0,
                Width = Dim.Percent(70),
                Height = Dim.Fill()
            };
            AddFormRow(form, "Script path", scriptPathField!, 1);
            AddFormRow(form, "Targets", targetsField!, 3);
            form.Add(new Label("Transport") { X = 1, Y = 5, Width = 20 });
            AddRetainedControl(form, transportList!);
            AddRetainedControl(form, runAsSystemBox!);
            AddRetainedControl(form, dryRunBox!);
            AddFormRow(form, "Throttle", throttleField!, 11);
            AddFormRow(form, "Expected exits", expectedExitField!, 13);
            AddFormRow(form, "Artifacts", artifactField!, 15);
            AddFormRow(form, "Output root", outputRootField!, 17);
            AddFormRow(form, "Remote root", remoteRootField!, 19);
            AddFormRow(form, "Script args", scriptArgumentsField!, 21);

            var actions = new FrameView("Launch Readiness")
            {
                X = Pos.Right(form),
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };
            readinessLabel = new Label("Ready") { X = 1, Y = 1, Width = Dim.Fill(2) };
            readinessBar = new ProgressBar { X = 1, Y = 3, Width = Dim.Fill(2), Fraction = GetReadinessFraction() };
            actions.Add(readinessLabel, readinessBar);
            AddActionButton(actions, "Launch", 6, LaunchRunFromControls);
            AddActionButton(actions, "Doctor", 9, ShowDoctor);
            AddActionButton(actions, "Home", 12, ShowHome);
            actions.Add(new Label("Ctrl+R launches. Esc returns home.")
            {
                X = 1,
                Y = 15,
                Width = Dim.Fill(2)
            });

            pane.Add(form, actions);
            UpdateReadiness();
            scriptPathField!.SetFocus();
            Application.Refresh();
        }

        private static void AddFormRow(View parent, string label, TextField field, int y)
        {
            parent.Add(new Label(label) { X = 1, Y = y, Width = 20 });
            field.X = 22;
            field.Y = y;
            field.Width = Dim.Fill(2);
            AddRetainedControl(parent, field);
        }

        private static void AddRetainedControl(View parent, View control)
        {
            control.SuperView?.Remove(control);
            parent.Add(control);
        }

        private static void AddActionButton(View parent, string text, int y, Action clicked)
        {
            var button = new Button(text)
            {
                X = 1,
                Y = y,
                Width = Dim.Fill(2)
            };
            button.Clicked += clicked;
            parent.Add(button);
        }

        private void EnsureRunControls()
        {
            scriptPathField ??= new TextField(string.Empty);
            targetsField ??= new TextField(string.Empty);
            transportList ??= new ListView(TransportChoices)
            {
                X = 22,
                Y = 5,
                Width = 18,
                Height = 3,
                SelectedItem = 0
            };
            runAsSystemBox ??= new CheckBox("Run as SYSTEM", false)
            {
                X = 1,
                Y = 9
            };
            dryRunBox ??= new CheckBox("Dry run", true)
            {
                X = 24,
                Y = 9
            };
            throttleField ??= new TextField(string.Empty);
            expectedExitField ??= new TextField("0");
            artifactField ??= new TextField(string.Empty);
            outputRootField ??= new TextField(string.Empty);
            remoteRootField ??= new TextField(string.Empty);
            scriptArgumentsField ??= new TextField(string.Empty);
        }

        private void LaunchRunFromControls()
        {
            EnsureRunControls();
            var scriptPath = GetText(scriptPathField);
            var targets = GetText(targetsField);
            if (string.IsNullOrWhiteSpace(scriptPath))
            {
                SetStatus("Run Setup", "Script path is required before a run can start.");
                UpdateReadiness();
                scriptPathField!.SetFocus();
                Application.Refresh();
                return;
            }

            if (string.IsNullOrWhiteSpace(targets))
            {
                SetStatus("Run Setup", "At least one computer name or target file entry is required before a run can start.");
                UpdateReadiness();
                targetsField!.SetFocus();
                Application.Refresh();
                return;
            }

            result = new CommandCenterResult(
                CommandCenterExitKind.StartRun,
                BuildRunArgumentsFromValues(
                    dryRunBox?.Checked is true,
                    scriptPath,
                    targets,
                    transportList?.SelectedItem ?? 0,
                    runAsSystemBox?.Checked is true,
                    GetText(expectedExitField),
                    GetText(throttleField),
                    GetText(artifactField),
                    GetText(outputRootField),
                    GetText(remoteRootField),
                    GetText(scriptArgumentsField)));
            Application.RequestStop();
        }

        private void ShowDoctor()
        {
            SetStatus("Doctor", "Running local prerequisite checks.");
            ResetWorkspace("Doctor");
            var report = doctor.Run();
            SetStatus("Doctor", report.Succeeded ? "Doctor checks passed." : "Doctor checks need attention.");
            var pane = RequireMainPane();
            var rows = report.Checks
                .Select(static check => $"{FormatDoctorStatus(check.Status),-4} {check.Name,-24} {check.Message} {check.Detail}".Trim())
                .DefaultIfEmpty("No doctor checks returned.")
                .ToList();
            pane.Add(new ListView(rows)
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(2),
                Height = Dim.Fill(2)
            });
            Application.Refresh();
        }

        private void ShowHelp()
        {
            SetStatus("Help", "Command reference loaded.");
            ResetWorkspace("Command Help");
            var lines = new[]
            {
                "dispatch                         Open the retained Terminal.Gui command center",
                @"dispatch run --script .\Fix.ps1 --computer-name PC001 --transport psexec",
                "dispatch doctor                  Check local prerequisites",
                "dispatch --version               Show installed version",
                string.Empty,
                "Automation reads durable result files. The terminal is an operator UI."
            };
            RequireMainPane().Add(new ListView(lines)
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(2),
                Height = Dim.Fill(2)
            });
            Application.Refresh();
        }

        private void ResetWorkspace(string title)
        {
            var pane = RequireMainPane();
            if (pane is FrameView frame)
            {
                frame.Title = title;
            }

            pane.RemoveAll();
        }

        private void SetStatus(string view, string message)
        {
            status.View = view;
            status.Message = message;
            if (viewLabel is not null)
            {
                viewLabel.Text = $"View: {view}";
            }

            if (messageLabel is not null)
            {
                messageLabel.Text = message;
            }
        }

        private void UpdateReadiness()
        {
            if (readinessBar is null || readinessLabel is null)
            {
                return;
            }

            var fraction = GetReadinessFraction();
            readinessBar.Fraction = fraction;
            readinessLabel.Text = fraction >= 1
                ? "Ready to launch"
                : "Missing script path or targets";
        }

        private float GetReadinessFraction()
        {
            var required = 0;
            if (!string.IsNullOrWhiteSpace(GetText(scriptPathField)))
            {
                required++;
            }

            if (!string.IsNullOrWhiteSpace(GetText(targetsField)))
            {
                required++;
            }

            return required / 2f;
        }

        private View RequireMainPane() =>
            mainPane ?? throw new InvalidOperationException("The command center shell has not been built.");

        private static string GetText(TextField? field) =>
            field?.Text?.ToString() ?? string.Empty;

        private sealed class StatusModel
        {
            public string View { get; set; } = "Home";

            public string Message { get; set; } = "Ready.";
        }
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

internal static class TerminalGuiViewExtensions
{
    public static View AddChained(this View view, params View[] children)
    {
        view.Add(children);
        return view;
    }
}
