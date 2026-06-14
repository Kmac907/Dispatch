using Dispatch.Core;
using Dispatch.Core.Configuration;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Dispatch.Transports.PsExec;
using Microsoft.Extensions.Options;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

namespace Dispatch.Cli;

public sealed class DispatchCliApplication(
    IOptions<DispatchOptions> options,
    IDispatchPlanner planner,
    IDispatchExecutor executor)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Any(static arg => arg is "--version" or "-v"))
        {
            Console.WriteLine(DispatchProduct.Version);
            return 0;
        }

        if (args.Length == 0)
        {
            return Console.IsInputRedirected
                ? await BuildParser(cancellationToken).InvokeAsync(["--help"]).ConfigureAwait(false)
                : await RunInteractiveAsync(cancellationToken).ConfigureAwait(false);
        }

        return await BuildParser(cancellationToken).InvokeAsync(args).ConfigureAwait(false);
    }

    private Parser BuildParser(CancellationToken cancellationToken)
    {
        var rootCommand = new RootCommand("Windows-native script orchestration for endpoint administrators.");
        rootCommand.AddCommand(BuildRunCommand(cancellationToken));
        rootCommand.AddCommand(BuildDoctorCommand());

        return new CommandLineBuilder(rootCommand)
            .UseDefaults()
            .Build();
    }

    private Command BuildRunCommand(CancellationToken cancellationToken)
    {
        var runCommand = new Command("run", "Run a PowerShell script across one or more Windows targets.");
        runCommand.AddOption(new Option<bool>("--dry-run", "Plan the run and print JSON without touching endpoints."));
        runCommand.AddOption(new Option<string>("--script", "Path to the PowerShell script to run."));
        runCommand.AddOption(new Option<string>("--computer-name", "Target name or comma-separated target names."));
        runCommand.AddOption(new Option<string>("--target-file", "Path to a target file."));
        runCommand.AddOption(new Option<string>("--transport", "Transport to use: psexec, psrp, or winrm."));
        runCommand.AddOption(new Option<string>("--expected-exit-code", "Expected exit code or comma-separated exit codes."));
        runCommand.AddOption(new Option<int?>("--throttle", "Maximum concurrent target executions."));
        runCommand.AddOption(new Option<bool>("--run-as-system", "Run the remote process as local SYSTEM when supported by the transport."));
        runCommand.AddOption(new Option<string>("--output-root", "Local root for Dispatch run results."));
        runCommand.AddOption(new Option<string>("--remote-root", "Remote Dispatch root for prepared scripts and outputs."));
        runCommand.AddOption(new Option<string>("--artifact-path", "Relative artifact path or comma-separated artifact paths to copy back."));
        runCommand.AddArgument(new Argument<string[]>("script-args", () => [], "Arguments passed through to the script.")
        {
            Arity = ArgumentArity.ZeroOrMore
        });
        runCommand.Description += Environment.NewLine + Environment.NewLine + PayloadBoundaryHelp;
        runCommand.TreatUnmatchedTokensAsErrors = false;
        runCommand.SetHandler(async context =>
        {
            var runArgs = RebuildRunArguments(context);
            context.ExitCode = await RunCommandAsync(runArgs, cancellationToken).ConfigureAwait(false);
        });

        return runCommand;
    }

    private static Command BuildDoctorCommand()
    {
        var doctorCommand = new Command("doctor", "Check local Dispatch prerequisites.");
        doctorCommand.SetHandler(static context =>
        {
            Console.WriteLine("dispatch doctor command surface is available.");
            Console.WriteLine("Detailed prerequisite checks are implemented in roadmap slice 6.1 Operator Diagnostics.");
            context.ExitCode = 0;
        });
        return doctorCommand;
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
            Console.Error.WriteLine(error);
            return 1;
        }

        try
        {
            var plan = await planner.CreatePlanAsync(command!.ToRequest(), cancellationToken).ConfigureAwait(false);
            if (command.DryRun)
            {
                Console.WriteLine(DispatchJson.Serialize(plan));
                return 0;
            }

            var result = await executor.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(DispatchJson.Serialize(result));
            WriteResultSummary(result);
            return result.FailedCount == 0 && result.TimedOutCount == 0 && result.CancelledCount == 0 ? 0 : 1;
        }
        catch (DispatchPlanningException exception)
        {
            foreach (var validationError in exception.Errors)
            {
                Console.Error.WriteLine($"{validationError.Code}: {validationError.Message}");
            }

            return 1;
        }
    }

    private async Task<int> RunInteractiveAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[bold]Dispatch {Markup.Escape(DispatchProduct.Version)}[/]");
        AnsiConsole.WriteLine("Windows-native script orchestration for endpoint administrators.");

        var scriptPath = PromptRequired("Script path");
        var computerNames = PromptRequired("Computer name(s)");
        var transport = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Transport")
                .AddChoices(PsExecTransportDescriptor.TransportName, "psrp", "winrm"));
        var runAsSystem = AnsiConsole.Confirm("Run as local SYSTEM?", defaultValue: false);
        var dryRun = AnsiConsole.Confirm("Dry run only?", defaultValue: true);
        var throttle = PromptOptionalInt("Throttle");
        var expectedExitCodes = PromptOptional("Expected exit code(s)", "0");
        var artifactPaths = PromptOptional("Artifact path(s)", string.Empty);
        var outputRoot = PromptOptional("Output root", string.Empty);
        var remoteRoot = PromptOptional("Remote root", string.Empty);
        var scriptArgs = PromptOptional("Script arguments", string.Empty);

        var table = new Table().RoundedBorder();
        table.AddColumn("Setting");
        table.AddColumn("Value");
        table.AddRow("Script", scriptPath);
        table.AddRow("Targets", computerNames);
        table.AddRow("Transport", transport);
        table.AddRow("Run as SYSTEM", runAsSystem ? "yes" : "no");
        table.AddRow("Dry run", dryRun ? "yes" : "no");
        table.AddRow("Throttle", throttle?.ToString() ?? "(default)");
        table.AddRow("Expected exit codes", expectedExitCodes);
        table.AddRow("Artifacts", string.IsNullOrWhiteSpace(artifactPaths) ? "(default)" : artifactPaths);
        AnsiConsole.Write(table);

        if (!AnsiConsole.Confirm("Start Dispatch run?", defaultValue: dryRun))
        {
            Console.Error.WriteLine("Dispatch run cancelled.");
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

    private static string PromptRequired(string title) =>
        AnsiConsole.Prompt(
            new TextPrompt<string>(title)
                .Validate(static value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("A value is required.")
                    : ValidationResult.Success()));

    private static string PromptOptional(string title, string defaultValue) =>
        AnsiConsole.Prompt(
            new TextPrompt<string>(title)
                .DefaultValue(defaultValue)
                .AllowEmpty());

    private static int? PromptOptionalInt(string title)
    {
        var value = PromptOptional(title, string.Empty);
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

    private static string[] RebuildRunArguments(InvocationContext context)
    {
        var tokens = context.ParseResult.Tokens
            .Select(static token => token.Value)
            .ToArray();

        return tokens.Length > 0 && tokens[0].Equals("run", StringComparison.OrdinalIgnoreCase)
            ? tokens[1..]
            : tokens;
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

    private static void WriteResultSummary(DispatchRunResult result)
    {
        Console.Error.WriteLine(
            $"Dispatch run {result.RunId}: {result.SuccessCount}/{result.TargetCount} succeeded, {result.FailedCount} failed, {result.TimedOutCount} timed out, {result.CancelledCount} cancelled. Results: {result.ResultPath}");
    }

    private const string PayloadBoundaryHelp = """
Payload boundary:
  Dispatch prepares only the selected script. Scripts own Blob, HTTPS, SMB, Azure Files,
  MSI, ZIP, and other external payload retrieval through ordinary non-secret script args.
  Do not pass credentials or SAS tokens on the command line.
""";
}
