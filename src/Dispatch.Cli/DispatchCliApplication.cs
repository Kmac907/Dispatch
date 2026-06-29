using Dispatch.Core;
using Dispatch.Core.Configuration;
using Dispatch.Core.Credentials;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Dispatch.Core.Targeting;
using Dispatch.Core.Transports;
using Dispatch.Transports.PsExec;
using Dispatch.Transports.Psrp;
using Dispatch.Transports.WinRm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dispatch.Cli;

public sealed class DispatchCliApplication(
    IOptions<DispatchOptions> options,
    IDispatchPlanner planner,
    IDispatchExecutor executor,
    IDispatchDoctor doctor,
    DispatchRunDisplayMode displayMode = DispatchRunDisplayMode.Auto,
    TextWriter? statusWriter = null,
    ICredentialProvider? credentialProvider = null,
    IRuntimeCredentialResolver? runtimeCredentialResolver = null,
    IRuntimeCredentialPrompt? runtimeCredentialPrompt = null,
    IWinRmScriptTransferClient? winRmTransferClient = null,
    IPsrpFileTransferClient? psrpFileTransferClient = null,
    IWinRmShellClient? winRmShellClient = null,
    IPsrpCommandClient? psrpCommandClient = null,
    IEnumerable<ITransportEndpointProbe>? endpointProbes = null)
{
    private readonly DispatchRunHistoryReader runHistoryReader = new();
    private readonly DispatchRunLogExporter runLogExporter = new();
    private readonly DispatchRunRetryPlanner runRetryPlanner = new();
    private readonly ICredentialProvider credentialProvider =
        credentialProvider ?? new UnavailableCredentialProvider(options);
    private readonly IRuntimeCredentialResolver runtimeCredentialResolver =
        runtimeCredentialResolver ?? new UnavailableRuntimeCredentialResolver();
    private readonly IRuntimeCredentialPrompt runtimeCredentialPrompt =
        runtimeCredentialPrompt ?? new UnavailableRuntimeCredentialPrompt();
    private readonly IWinRmScriptTransferClient? winRmTransferClient = winRmTransferClient;
    private readonly IPsrpFileTransferClient? psrpFileTransferClient = psrpFileTransferClient;
    private readonly IWinRmShellClient? winRmShellClient = winRmShellClient;
    private readonly IPsrpCommandClient? psrpCommandClient = psrpCommandClient;
    private readonly IReadOnlyDictionary<TransportKind, ITransportEndpointProbe> endpointProbes =
        (endpointProbes ?? [])
            .GroupBy(static probe => probe.Kind)
            .ToDictionary(static group => group.Key, static group => group.First());

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Any(static arg => arg is "--version" or "-v"))
        {
            SpectreConsoleRenderer.RenderVersion(Console.Out);
            return 0;
        }

        if (args.Length == 0)
        {
            return RenderRootHelp();
        }

        if (IsRootHelpRequest(args))
        {
            return RenderRootHelp();
        }

        var commandName = args[0].ToLowerInvariant();
        if (commandName == "run" && IsExplicitHelpRequest(args.Skip(1).ToArray()))
        {
            return RenderRunHelp();
        }

        if (commandName == "doctor" && IsExplicitHelpRequest(args.Skip(1).ToArray()))
        {
            return RenderDoctorHelp();
        }

        if (commandName == "run" && args.Length == 1)
        {
            return RenderRunHelp();
        }

        if (commandName == "run" && IsLegacyRunCompatibilityRequest(args))
        {
            return await RunCommandAsync(args[1..], cancellationToken).ConfigureAwait(false);
        }

        if (commandName == "creds" && TryFindPlaintextCredentialOption(args.Skip(1), out var plaintextOption))
        {
            return RenderInvalidCommand(
                $"Plaintext credential option '{plaintextOption}' is not supported. Use a configured credential provider; Dispatch will not accept passwords on the command line.");
        }

        if (IsSpectreRegisteredCommand(commandName))
        {
            return await new DispatchSpectreCommandApp(this).RunAsync(args, cancellationToken).ConfigureAwait(false);
        }

        return RenderUnknownCommand(args[0]);

    }

    internal async Task<int> RunCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!DispatchRunCommandParser.TryParse(
                args,
                new DispatchRunCommandParser.DispatchRunAmbientConfig(
                    options.Value.Inventory,
                    options.Value.Target,
                    options.Value.Exclude,
                    options.Value.DefaultTransport),
                options.Value.ExpectedExitCodes,
                out var command,
                out var error))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", error);
            return 1;
        }

        return await RunParsedCommandAsync(command!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RunParsedCommandAsync(
        DispatchRunCommand command,
        CancellationToken cancellationToken)
    {
        var outcome = await RunParsedCommandAsync(command, renderOutput: true, cancellationToken).ConfigureAwait(false);
        return outcome.ExitCode;
    }

    private async Task<DispatchRunCommandOutcome> RunParsedCommandAsync(
        DispatchRunCommand command,
        bool renderOutput,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await TryValidateCredentialReferenceAsync(command, cancellationToken).ConfigureAwait(false))
            {
                return new DispatchRunCommandOutcome(1, null);
            }

            var request = command.ToRequest();
            if (command.DryRun)
            {
                if (command.OutputMode == DispatchOutputMode.Ndjson)
                {
                    var streamWriter = new DispatchNdjsonStreamWriter(Console.Out, command.Verbose, command.Trace);
                    streamWriter.WritePlanningStarted();
                    var ndjsonPlan = await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
                    streamWriter.WritePlan(ndjsonPlan);
                    return new DispatchRunCommandOutcome(0, null);
                }

                var dryRunPlan = command.OutputMode == DispatchOutputMode.Rich && !command.Quiet
                    ? await CreatePlanWithDryRunProgressAsync(request, command.NoColor, cancellationToken).ConfigureAwait(false)
                    : await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
                if (renderOutput && !ShouldSuppressOutput(command))
                {
                    DispatchStructuredOutputRenderer.RenderPlan(Console.Out, dryRunPlan, command.OutputMode);
                }

                return new DispatchRunCommandOutcome(0, null);
            }

            if (command.OutputMode == DispatchOutputMode.Ndjson)
            {
                var streamWriter = new DispatchNdjsonStreamWriter(Console.Out, command.Verbose, command.Trace);
                streamWriter.WritePlanningStarted();
                var streamPlan = await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
                streamWriter.WritePlan(streamPlan);
                if (HasScriptSecrets(streamPlan))
                {
                    RenderScriptSecretHandoffPlanned();
                    return new DispatchRunCommandOutcome(1, null);
                }

                var resolvedStreamPlan = await ResolveRuntimeCredentialsAsync(streamPlan, command, cancellationToken).ConfigureAwait(false);
                if (resolvedStreamPlan is null)
                {
                    return new DispatchRunCommandOutcome(1, null);
                }

                try
                {
                    streamWriter.WriteExecutionStarted(resolvedStreamPlan);
                    var streamResult = await executor.ExecuteAsync(resolvedStreamPlan, streamWriter, cancellationToken).ConfigureAwait(false);
                    streamWriter.WriteResult(streamResult);
                    return new DispatchRunCommandOutcome(GetRunResultExitCode(streamResult), streamResult);
                }
                finally
                {
                    DisposeRuntimeCredentials(resolvedStreamPlan);
                }
            }

            var plan = command.OutputMode == DispatchOutputMode.Rich && !command.Quiet
                ? await CreatePlanWithStatusAsync(request, command.NoColor, cancellationToken).ConfigureAwait(false)
                : await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
            if (HasScriptSecrets(plan))
            {
                RenderScriptSecretHandoffPlanned();
                return new DispatchRunCommandOutcome(1, null);
            }

            var resolvedPlan = await ResolveRuntimeCredentialsAsync(plan, command, cancellationToken).ConfigureAwait(false);
            if (resolvedPlan is null)
            {
                return new DispatchRunCommandOutcome(1, null);
            }

            try
            {
                var result = command.OutputMode == DispatchOutputMode.Rich && !command.Quiet
                    ? await RunWithSpectreProgressAsync(resolvedPlan, command.NoDashboard, command.NoColor, cancellationToken).ConfigureAwait(false)
                    : await executor.ExecuteAsync(resolvedPlan, NullDispatchExecutionObserver.Instance, cancellationToken).ConfigureAwait(false);
                if (renderOutput && !ShouldSuppressOutput(command))
                {
                    DispatchStructuredOutputRenderer.RenderRunResult(Console.Out, result, command.OutputMode);
                }

                return new DispatchRunCommandOutcome(GetRunResultExitCode(result), result);
            }
            finally
            {
                DisposeRuntimeCredentials(resolvedPlan);
            }
        }
        catch (DispatchPlanningException exception)
        {
            var message = string.Join(
                Environment.NewLine,
                exception.Errors.Select(static validationError => $"{validationError.Code}: {validationError.Message}"));
            SpectreConsoleRenderer.RenderError(Console.Error, "Dispatch Planning Failed", message);

            return new DispatchRunCommandOutcome(1, null);
        }
    }

    private static int GetRunResultExitCode(DispatchRunResult result) =>
        result.FailedCount == 0 && result.TimedOutCount == 0 && result.CancelledCount == 0 ? 0 : 1;

    private static bool HasScriptSecrets(ExecutionPlan plan) => plan.Job.ScriptSecrets.Count > 0;

    private static void RenderScriptSecretHandoffPlanned() =>
        RenderPlannedCommand(
            "run ps --secret",
            "10 Script-Owned Payload Documentation And Guardrails - safe script-secret parameter binding");

    private async Task<DispatchRunResult> RunWithSpectreProgressAsync(
        ExecutionPlan plan,
        bool noDashboard,
        bool noColor,
        CancellationToken cancellationToken)
    {
        var useLiveDisplay = ShouldUseLiveDashboard(noDashboard);
        if (!useLiveDisplay && Console.IsErrorRedirected && statusWriter is null)
        {
            return await executor.ExecuteAsync(plan, NullDispatchExecutionObserver.Instance, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            var renderer = new SpectreLiveRunRenderer(
                plan,
                executor,
                statusWriter ?? Console.Error,
                useLiveDisplay,
                noColor);
            return await renderer.ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException exception) when (displayMode == DispatchRunDisplayMode.Auto && useLiveDisplay)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Live Dashboard Unavailable",
                $"Dispatch is using append-only Spectre progress for this run. {exception.Message}");
            var renderer = new SpectreLiveRunRenderer(
                plan,
                executor,
                statusWriter ?? Console.Error,
                useLiveDisplay: false,
                noColor);
            return await renderer.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ExecutionPlan> CreatePlanWithStatusAsync(
        DispatchRequest request,
        bool noColor,
        CancellationToken cancellationToken) =>
        await SpectreConsoleRenderer.RunPlanningStatusAsync(
                Console.Out,
                token => planner.CreatePlanAsync(request, token),
                noColor,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<ExecutionPlan> CreatePlanWithDryRunProgressAsync(
        DispatchRequest request,
        bool noColor,
        CancellationToken cancellationToken) =>
        await SpectreConsoleRenderer.RunDryRunPlanningProgressAsync(
                Console.Out,
                token => planner.CreatePlanAsync(request, token),
                noColor,
                cancellationToken)
            .ConfigureAwait(false);

    private static bool ShouldSuppressOutput(DispatchRunCommand command) =>
        command.Quiet && command.OutputMode is DispatchOutputMode.Rich or DispatchOutputMode.Table;

    private async Task<ExecutionPlan?> ResolveRuntimeCredentialsAsync(
        ExecutionPlan plan,
        DispatchRunCommand command,
        CancellationToken cancellationToken)
    {
        var credentialReferences = plan.Targets
            .Select(static target => target.Target.CredentialReference)
            .Where(static reference => !string.IsNullOrWhiteSpace(reference))
            .Select(static reference => reference!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (credentialReferences.Length == 0)
        {
            return plan;
        }

        if (plan.Job.Transport is not (TransportKind.Psrp or TransportKind.WinRm))
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Credential Handoff Unsupported",
                $"Runtime credential handoff is currently implemented for PSRP and raw WinRM only. Transport '{plan.Job.Transport.ToDispatchString()}' cannot use credential reference '{credentialReferences[0]}'.");
            return null;
        }

        var resolver = CreateRuntimeCredentialResolverForCommand(command);
        var result = await resolver
            .ResolveAsync(credentialReferences, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Credential Resolution Failed",
                result.FailureMessage ?? "Credential resolution failed.");
            return null;
        }

        return plan with { RuntimeCredentials = result.Credentials };
    }

    private static void DisposeRuntimeCredentials(ExecutionPlan plan)
    {
        foreach (var credential in plan.RuntimeCredentials.Values)
        {
            credential.Dispose();
        }
    }

    private bool ShouldUseLiveDashboard(bool noDashboard) =>
        displayMode switch
        {
            DispatchRunDisplayMode.LiveDashboard => true,
            DispatchRunDisplayMode.AppendOnly => false,
            _ => !noDashboard && !Console.IsErrorRedirected
        };

    private static bool IsRootHelpRequest(IReadOnlyList<string> args) =>
        args.Count == 1 && args[0] is "--help" or "-h" or "-?";

    private static bool IsExplicitHelpRequest(IReadOnlyList<string> args) =>
        args.Any(static arg => arg is "--help" or "-h" or "-?");

    private static bool IsSpectreRegisteredCommand(string command) =>
        command is "apply" or "run" or "push" or "hosts" or "logs" or "creds" or "doctor" or "init" or "version";

    private static bool IsLegacyRunCompatibilityRequest(IReadOnlyList<string> args) =>
        args.Count > 1 && !args[1].Equals("ps", StringComparison.OrdinalIgnoreCase)
                       && !args[1].Equals("cmd", StringComparison.OrdinalIgnoreCase)
                       && !args[1].Equals("exe", StringComparison.OrdinalIgnoreCase);

    private static bool TryFindPlaintextCredentialOption(IEnumerable<string> args, out string? option)
    {
        option = args.FirstOrDefault(static arg =>
            arg.Equals("--password", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("--password=", StringComparison.OrdinalIgnoreCase));
        return option is not null;
    }

    private static int RenderRootHelp()
    {
        SpectreConsoleRenderer.RenderRootHelp(Console.Out);
        return 0;
    }

    internal static int RenderVersion()
    {
        SpectreConsoleRenderer.RenderVersion(Console.Out);
        return 0;
    }

    private static int RenderRunHelp()
    {
        SpectreConsoleRenderer.RenderRunHelp(Console.Out);
        return 0;
    }

    private static int RenderDoctorHelp()
    {
        SpectreConsoleRenderer.RenderDoctorHelp(Console.Out);
        return 0;
    }

    internal int RunDoctorCommand()
    {
        var report = doctor.Run();
        SpectreConsoleRenderer.RenderDoctorReport(Console.Out, report);
        return report.Succeeded ? 0 : 1;
    }

    internal async Task<int> RunPushCommandAsync(
        string? source,
        string? destination,
        bool plan,
        bool check,
        bool recurse,
        bool checksum,
        bool overwrite,
        bool backup,
        bool execute,
        bool cleanup,
        string? inventory,
        string? target,
        string? exclude,
        string? transport,
        string? credentialReference,
        int? concurrency,
        string? configPath,
        string? outputValue,
        bool noColor,
        bool noProgress,
        bool quiet,
        bool verbose,
        bool trace,
        CancellationToken cancellationToken)
    {
        if (!TryParseOutputMode(outputValue, out var outputMode, out var outputError))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Push", outputError!);
            return 1;
        }

        if (plan && check)
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Push", "--plan and --check cannot be used together.");
            return 1;
        }

        if (!TryBuildPushPlan(
                source,
                destination,
                recurse,
                checksum,
                overwrite,
                backup,
                execute,
                cleanup,
                inventory,
                target,
                exclude,
                transport,
                credentialReference,
                concurrency,
                configPath,
                outputMode,
                out var pushPlan,
                out var error))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Push", error);
            return 1;
        }

        if (plan || check)
        {
            DispatchStructuredOutputRenderer.RenderPushPlan(Console.Out, pushPlan!, outputMode);
            return 0;
        }

        if (pushPlan!.Transport == TransportKind.WinRm && winRmTransferClient is null)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Push Failed",
                "Raw WinRM upload support is unavailable in this Dispatch runtime.");
            return 1;
        }

        if (pushPlan.Transport == TransportKind.Psrp && psrpFileTransferClient is null)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Push Failed",
                "PSRP upload support is unavailable in this Dispatch runtime.");
            return 1;
        }

        var result = await ExecutePushPlanAsync(pushPlan, noProgress, cancellationToken).ConfigureAwait(false);
        DispatchStructuredOutputRenderer.RenderPushResult(Console.Out, result, outputMode);
        return result.Succeeded ? 0 : 2;
    }

    internal async Task<int> RunApplyCommandAsync(
        string? jobPath,
        bool plan,
        bool check,
        string? configPath,
        string? credentialReference,
        string? transport,
        string? inventory,
        string? target,
        string? exclude,
        string? tags,
        string? skipTags,
        int? serial,
        bool diff,
        string? outputValue,
        bool noColor,
        bool noProgress,
        bool quiet,
        bool verbose,
        bool trace,
        CancellationToken cancellationToken)
    {
        if (!TryParseOutputMode(outputValue, out var outputMode, out var outputError))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", outputError!);
            return 1;
        }

        if (!DispatchApplyJobParser.TryParse(
                jobPath ?? string.Empty,
                new DispatchApplyJobParser.ApplyCommandOptions(
                    Plan: plan,
                    Check: check,
                    ConfigPath: configPath,
                    CredentialReference: credentialReference,
                    Transport: transport,
                    Inventory: inventory,
                    Target: target,
                    Exclude: exclude,
                    Tags: tags,
                    SkipTags: skipTags,
                    Serial: serial,
                    Diff: diff,
                    OutputMode: outputMode,
                    NoColor: noColor,
                    NoProgress: noProgress,
                    Quiet: quiet,
                    Verbose: verbose,
                    Trace: trace),
                new DispatchRunCommandParser.DispatchRunAmbientConfig(
                    options.Value.Inventory,
                    options.Value.Target,
                    options.Value.Exclude,
                    options.Value.DefaultTransport),
                options.Value.ExpectedExitCodes,
                out var apply,
                out var error))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Job", error);
            return 1;
        }

        if (apply!.Mode == "execute" && apply.Tasks.Count == 1)
        {
            return await RunParsedCommandAsync(apply.Tasks[0].Command!, cancellationToken).ConfigureAwait(false);
        }

        if (apply.Mode == "execute")
        {
            return await RunApplyExecutionAsync(apply, cancellationToken).ConfigureAwait(false);
        }

        return await RunApplyPlanAsync(apply, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RunApplyExecutionAsync(
        DispatchApplyJobParser.ApplyParseResult apply,
        CancellationToken cancellationToken)
    {
        var executedTasks = new List<DispatchApplyExecutedTask>();
        var exitCode = 0;
        var firstCommand = apply.Tasks.Select(static task => task.Command).First(static command => command is not null)!;

        foreach (var task in apply.Tasks)
        {
            var renderTaskOutput = firstCommand.OutputMode == DispatchOutputMode.Ndjson;
            var command = task.Command!;
            var outcome = await RunParsedCommandAsync(command, renderTaskOutput, cancellationToken).ConfigureAwait(false);
            if (outcome.Result is null)
            {
                return outcome.ExitCode;
            }

            executedTasks.Add(new DispatchApplyExecutedTask(
                task.Index,
                task.Type,
                GetApplyTaskScriptPath(command.Payload),
                GetApplyTaskCommandLine(command.Payload),
                task.Tags,
                outcome.Result));
            if (outcome.ExitCode != 0)
            {
                exitCode = outcome.ExitCode;
                break;
            }
        }

        if (!ShouldSuppressOutput(firstCommand))
        {
            DispatchStructuredOutputRenderer.RenderApplyExecution(
                Console.Out,
                new DispatchApplyExecution("execute", executedTasks),
                firstCommand.OutputMode);
        }

        return exitCode;
    }

    private async Task<int> RunApplyPlanAsync(
        DispatchApplyJobParser.ApplyParseResult apply,
        CancellationToken cancellationToken)
    {
        foreach (var task in apply.Tasks)
        {
            if (task.Command is not null
                && !await TryValidateCredentialReferenceAsync(task.Command, cancellationToken).ConfigureAwait(false))
            {
                return 1;
            }
        }

        var plannedTasks = new List<DispatchApplyPlannedTask>();
        try
        {
            foreach (var task in apply.Tasks)
            {
                if (task.Copy is { } copy)
                {
                    plannedTasks.Add(new DispatchApplyPlannedTask(
                        task.Index,
                        task.Type,
                        null,
                        null,
                        copy.SourcePath,
                        copy.DestinationPath,
                        copy.Overwrite,
                        copy.Transport,
                        copy.Targets.Select(static target => target.Name).ToArray(),
                        task.Tags,
                        null));
                    continue;
                }

                var command = task.Command!;
                var plan = await planner
                    .CreatePlanAsync(command.ToRequest(), cancellationToken)
                    .ConfigureAwait(false);
                plannedTasks.Add(new DispatchApplyPlannedTask(
                    task.Index,
                    task.Type,
                    GetApplyTaskScriptPath(command.Payload),
                    GetApplyTaskCommandLine(command.Payload),
                    null,
                    null,
                    null,
                    null,
                    null,
                    task.Tags,
                    plan));
            }
        }
        catch (DispatchPlanningException exception)
        {
            var message = string.Join(
                Environment.NewLine,
                exception.Errors.Select(static validationError => $"{validationError.Code}: {validationError.Message}"));
            SpectreConsoleRenderer.RenderError(Console.Error, "Dispatch Planning Failed", message);

            return 1;
        }

        if (!ShouldSuppressApplyOutput(apply))
        {
            DispatchStructuredOutputRenderer.RenderApplyPlan(
                Console.Out,
                new DispatchApplyPlan(apply.Mode, plannedTasks),
                apply.OutputMode);
        }

        return 0;
    }

    private static bool ShouldSuppressApplyOutput(DispatchApplyJobParser.ApplyParseResult apply) =>
        apply.Quiet && apply.OutputMode is DispatchOutputMode.Rich or DispatchOutputMode.Table;

    private static string? GetApplyTaskScriptPath(DispatchPayload payload) =>
        payload is ScriptPayload script ? script.ScriptPath : null;

    private static string? GetApplyTaskCommandLine(DispatchPayload payload) =>
        payload is CommandPayload command ? command.CommandLine : null;

    internal int RunLogsListCommand(string? outputValue)
    {
        if (!TryParseOutputMode(outputValue, out var outputMode, out var error))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", error!);
            return 1;
        }

        var runs = runHistoryReader.ListRuns(options.Value.LocalRunRoot);
        if (runs.Count == 0)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Logs Not Found",
                $"No completed runs were found under '{options.Value.LocalRunRoot}'.");
            return 1;
        }

        DispatchStructuredOutputRenderer.RenderRunHistory(Console.Out, options.Value.LocalRunRoot, runs, outputMode);
        return 0;
    }

    internal int RunLogsShowCommand(string? selector, string? outputValue)
    {
        if (!TryParseOutputMode(outputValue, out var outputMode, out var error))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", error!);
            return 1;
        }

        var result = runHistoryReader.ReadRun(options.Value.LocalRunRoot, selector);
        if (result is null)
        {
            var missingSelector = string.IsNullOrWhiteSpace(selector) ? "latest" : selector;
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Logs Not Found",
                $"Run '{missingSelector}' was not found under '{options.Value.LocalRunRoot}'.");
            return 1;
        }

        DispatchStructuredOutputRenderer.RenderRunResult(Console.Out, result, outputMode);
        return 0;
    }

    internal int RunLogsTailCommand(string? selector, int? count, string? outputValue)
    {
        if (!TryParseOutputMode(outputValue, out var outputMode, out var error))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", error!);
            return 1;
        }

        var tailCount = count ?? 20;
        if (tailCount <= 0)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Invalid Dispatch Command",
                "Tail count must be greater than zero.");
            return 1;
        }

        DispatchRunEventTail? tail;
        try
        {
            tail = runHistoryReader.ReadRunEvents(options.Value.LocalRunRoot, selector, tailCount);
        }
        catch (JsonException exception)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Logs Invalid",
                $"The durable event stream could not be parsed. {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Logs Unavailable",
                $"The durable event stream could not be read. {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Logs Unavailable",
                $"The durable event stream could not be read. {exception.Message}");
            return 1;
        }

        if (tail is null)
        {
            var missingSelector = string.IsNullOrWhiteSpace(selector) ? "latest" : selector;
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Logs Not Found",
                $"Run '{missingSelector}' was not found under '{options.Value.LocalRunRoot}'.");
            return 1;
        }

        DispatchStructuredOutputRenderer.RenderRunEventTail(Console.Out, tail, outputMode);
        return 0;
    }

    internal int RunLogsExportCommand(string? selector, string? destination, string? outputValue)
    {
        if (!TryParseOutputMode(outputValue, out var outputMode, out var error))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", error!);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Invalid Dispatch Command",
                "logs export requires --dest <path>.");
            return 1;
        }

        var run = runHistoryReader.ReadRunEntry(options.Value.LocalRunRoot, selector);
        if (run is null)
        {
            var missingSelector = string.IsNullOrWhiteSpace(selector) ? "latest" : selector;
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Logs Not Found",
                $"Run '{missingSelector}' was not found under '{options.Value.LocalRunRoot}'.");
            return 1;
        }

        DispatchRunLogExportResult exportResult;
        try
        {
            exportResult = runLogExporter.Export(run, destination);
        }
        catch (IOException exception)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Logs Export Failed",
                $"The run log export could not be written. {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Logs Export Failed",
                $"The run log export could not be written. {exception.Message}");
            return 1;
        }

        DispatchStructuredOutputRenderer.RenderRunLogExport(Console.Out, exportResult, outputMode);
        return 0;
    }

    internal int RunLogsRetryCommand(string? selector, string? outputValue)
    {
        if (!TryParseOutputMode(outputValue, out var outputMode, out var error))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", error!);
            return 1;
        }

        var run = runHistoryReader.ReadRunEntry(options.Value.LocalRunRoot, selector);
        if (run is null)
        {
            var missingSelector = string.IsNullOrWhiteSpace(selector) ? "latest" : selector;
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Logs Not Found",
                $"Run '{missingSelector}' was not found under '{options.Value.LocalRunRoot}'.");
            return 1;
        }

        var retryPlan = runRetryPlanner.Create(run);
        DispatchStructuredOutputRenderer.RenderRunRetryPlan(Console.Out, retryPlan, outputMode);
        return 0;
    }

    internal int RunHostsListCommand(string? inventory, string? outputValue)
    {
        if (!TryParseOutputMode(outputValue, out var outputMode, out var outputError))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", outputError!);
            return 1;
        }

        if (!TryInspectInventory(inventory, out var inspection))
        {
            return 1;
        }

        DispatchStructuredOutputRenderer.RenderHostInventoryInspection(Console.Out, inspection!, outputMode);
        return 0;
    }

    internal int RunHostsValidateCommand(string? inventory, string? outputValue)
    {
        if (!TryParseOutputMode(outputValue, out var outputMode, out var outputError))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", outputError!);
            return 1;
        }

        if (!TryInspectInventory(inventory, out var inspection))
        {
            return 1;
        }

        DispatchStructuredOutputRenderer.RenderHostInventoryValidation(Console.Out, inspection!, outputMode);
        return 0;
    }

    internal int RunHostsGraphCommand(string? inventory, string? outputValue)
    {
        if (!TryParseOutputMode(outputValue, out var outputMode, out var outputError))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", outputError!);
            return 1;
        }

        if (!TryInspectInventoryGraph(inventory, out var graph))
        {
            return 1;
        }

        DispatchStructuredOutputRenderer.RenderHostInventoryGraph(Console.Out, graph!, outputMode);
        return 0;
    }

    internal int RunHostsVarsCommand(string? inventory, string? target, string? outputValue)
    {
        if (!TryParseOutputMode(outputValue, out var outputMode, out var outputError))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", outputError!);
            return 1;
        }

        var normalizedTarget = NormalizeOptionalValue(target);
        if (normalizedTarget is null)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Invalid Dispatch Hosts",
                "hosts vars requires --target <host>.");
            return 1;
        }

        if (!TryInspectInventory(inventory, out var inspection))
        {
            return 1;
        }

        var host = inspection!.Hosts.FirstOrDefault(item =>
            item.Name.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase));
        if (host is null)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Invalid Dispatch Hosts",
                $"Inventory '{inspection.InventoryPath}' does not contain host '{normalizedTarget}'.");
            return 1;
        }

        DispatchStructuredOutputRenderer.RenderHostVars(
            Console.Out,
            new DispatchHostVarsResult(inspection.InventoryPath, normalizedTarget, host),
            outputMode);
        return 0;
    }

    internal async Task<int> RunHostsTestCommandAsync(
        string? inventory,
        string? target,
        string? exclude,
        string? transport,
        string? configPath,
        string? outputValue,
        CancellationToken cancellationToken)
    {
        if (!TryParseOutputMode(outputValue, out var outputMode, out var outputError))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Hosts", outputError!);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Invalid Dispatch Hosts",
                "hosts test requires --target <selector>.");
            return 1;
        }

        if (!TryLoadPushConfig(configPath, out var config, out var configError))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Hosts", configError);
            return 1;
        }

        var inventoryPath = NormalizeOptionalValue(inventory) ?? NormalizeOptionalValue(config.Inventory);
        if (inventoryPath is null)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Invalid Dispatch Hosts",
                "hosts test requires --inventory <path> or Dispatch:Inventory in configuration.");
            return 1;
        }

        var targetResolution = TargetResolver.Resolve(new TargetResolutionInput(
            ComputerNameValues: [],
            TargetFile: null,
            InventoryPath: inventoryPath,
            TargetSelectors: [target],
            ExcludeSelectors: string.IsNullOrWhiteSpace(exclude) ? [] : [exclude]));
        if (targetResolution.Errors.Count > 0)
        {
            var message = string.Join(
                Environment.NewLine,
                targetResolution.Errors.Select(static validationError => $"{validationError.Code}: {validationError.Message}"));
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Hosts", message);
            return 1;
        }

        if (!TryResolveHostsTestTransports(transport, targetResolution, config.DefaultTransport, out var targetTransports, out var transportError))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Hosts", transportError);
            return 1;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var results = new List<DispatchHostTestTargetResult>();
        foreach (var resolvedTarget in targetResolution.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolvedTransport = targetTransports[resolvedTarget.Name];
            if (!endpointProbes.TryGetValue(resolvedTransport, out var probe))
            {
                var missingStartedAt = DateTimeOffset.UtcNow;
                results.Add(new DispatchHostTestTargetResult(
                    resolvedTarget.Name,
                    resolvedTransport,
                    false,
                    FailureCategory.TransportUnavailable,
                    $"No endpoint probe is registered for transport '{resolvedTransport.ToDispatchString()}'.",
                    missingStartedAt,
                    DateTimeOffset.UtcNow,
                    new Dictionary<string, string>()));
                continue;
            }

            var plan = CreateHostsTestProbePlan(resolvedTarget, resolvedTransport);
            var probeResult = await probe
                .ProbeAsync(new TransportEndpointProbeRequest(plan, plan.Targets[0]), cancellationToken)
                .ConfigureAwait(false);
            results.Add(new DispatchHostTestTargetResult(
                resolvedTarget.Name,
                resolvedTransport,
                probeResult.Succeeded,
                probeResult.FailureCategory,
                probeResult.FailureMessage,
                probeResult.StartedAt,
                probeResult.EndedAt,
                probeResult.Metadata ?? new Dictionary<string, string>()));
        }

        var result = new DispatchHostTestResult(
            inventoryPath,
            target,
            NormalizeOptionalValue(exclude),
            NormalizeOptionalValue(transport) ?? "auto",
            startedAt,
            DateTimeOffset.UtcNow,
            results);
        DispatchStructuredOutputRenderer.RenderHostTestResult(Console.Out, result, outputMode);
        return result.Succeeded ? 0 : 1;
    }

    internal async Task<int> RunCredsAddCommandAsync(
        string name,
        string? userName,
        bool force,
        string? outputValue,
        CancellationToken cancellationToken)
    {
        if (!TryValidateCredentialName(name, out var nameError))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", nameError!);
            return 1;
        }

        if (!TryParseOutputMode(outputValue, out var outputMode, out var outputError))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", outputError!);
            return 1;
        }

        var result = await credentialProvider
            .AddAsync(new CredentialAddRequest(name.Trim(), NormalizeOptionalValue(userName), force), cancellationToken)
            .ConfigureAwait(false);
        return RenderCredentialOperationResult(result, outputMode);
    }

    internal async Task<int> RunCredsListCommandAsync(string? outputValue, CancellationToken cancellationToken)
    {
        if (!TryParseOutputMode(outputValue, out var outputMode, out var error))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", error!);
            return 1;
        }

        var result = await credentialProvider.ListAsync(cancellationToken).ConfigureAwait(false);
        return RenderCredentialOperationResult(result, outputMode);
    }

    internal async Task<int> RunCredsTestCommandAsync(
        string name,
        string? outputValue,
        CancellationToken cancellationToken)
    {
        if (!TryValidateCredentialName(name, out var nameError))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", nameError!);
            return 1;
        }

        if (!TryParseOutputMode(outputValue, out var outputMode, out var outputError))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", outputError!);
            return 1;
        }

        var result = await credentialProvider
            .TestAsync(new CredentialReferenceRequest(name.Trim()), cancellationToken)
            .ConfigureAwait(false);
        return RenderCredentialOperationResult(result, outputMode);
    }

    internal async Task<int> RunCredsRemoveCommandAsync(
        string name,
        string? outputValue,
        CancellationToken cancellationToken)
    {
        if (!TryValidateCredentialName(name, out var nameError))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", nameError!);
            return 1;
        }

        if (!TryParseOutputMode(outputValue, out var outputMode, out var outputError))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", outputError!);
            return 1;
        }

        var result = await credentialProvider
            .RemoveAsync(new CredentialReferenceRequest(name.Trim()), cancellationToken)
            .ConfigureAwait(false);
        return RenderCredentialOperationResult(result, outputMode);
    }

    internal int RunInitCommand(DispatchInitScaffold scaffold, string? outputDirectory = null)
    {
        var templates = GetInitTemplates(scaffold);
        var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(outputDirectory)
            ? Directory.GetCurrentDirectory()
            : outputDirectory);
        var targets = templates
            .Select(template => template with { Path = Path.Combine(directory, template.FileName) })
            .ToArray();

        if (!Directory.Exists(directory))
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Init Failed",
                $"Directory '{directory}' does not exist.");
            return 1;
        }

        var existing = targets.FirstOrDefault(static target =>
            File.Exists(target.Path) || Directory.Exists(target.Path));
        if (existing is not null)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Init Failed",
                $"Refusing to overwrite existing path '{existing.Path}'.");
            return 1;
        }

        try
        {
            foreach (var target in targets)
            {
                using var stream = new FileStream(target.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(target.Content);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Dispatch Init Failed",
                $"Starter files could not be written. {exception.Message}");
            return 1;
        }

        foreach (var target in targets)
        {
            Console.Out.WriteLine($"Created {target.Path}");
        }

        return 0;
    }

    internal static int RenderPlannedCommand(string command, string roadmapItem)
    {
        SpectreConsoleRenderer.RenderPlannedFeature(Console.Error, command, roadmapItem);
        return 1;
    }

    internal static int RenderInvalidCommand(string message)
    {
        SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Command", message);
        return 1;
    }

    private bool TryInspectInventory(string? inventory, out InventoryInspectionResult? inspection)
    {
        inspection = null;
        var inventoryPath = NormalizeOptionalValue(inventory) ?? NormalizeOptionalValue(options.Value.Inventory);
        if (inventoryPath is null)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Invalid Dispatch Hosts",
                "hosts commands require --inventory <path> or Dispatch:Inventory in configuration.");
            return false;
        }

        inspection = TargetResolver.InspectInventory(inventoryPath);
        if (inspection.IsValid)
        {
            return true;
        }

        var message = string.Join(
            Environment.NewLine,
            inspection.Errors.Select(static validationError => $"{validationError.Code}: {validationError.Message}"));
        SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Hosts", message);
        return false;
    }

    private bool TryInspectInventoryGraph(string? inventory, out InventoryGraphInspectionResult? graph)
    {
        graph = null;
        var inventoryPath = NormalizeOptionalValue(inventory) ?? NormalizeOptionalValue(options.Value.Inventory);
        if (inventoryPath is null)
        {
            SpectreConsoleRenderer.RenderError(
                Console.Error,
                "Invalid Dispatch Hosts",
                "hosts commands require --inventory <path> or Dispatch:Inventory in configuration.");
            return false;
        }

        graph = TargetResolver.InspectInventoryGraph(inventoryPath);
        if (graph.IsValid)
        {
            return true;
        }

        var message = string.Join(
            Environment.NewLine,
            graph.Errors.Select(static validationError => $"{validationError.Code}: {validationError.Message}"));
        SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Hosts", message);
        return false;
    }

    private static bool TryResolveHostsTestTransports(
        string? transport,
        TargetResolutionResult targetResolution,
        TransportKind? defaultTransport,
        out IReadOnlyDictionary<string, TransportKind> targetTransports,
        out string error)
    {
        var resolved = new Dictionary<string, TransportKind>(StringComparer.OrdinalIgnoreCase);
        targetTransports = resolved;
        error = string.Empty;

        var normalized = NormalizeOptionalValue(transport);
        if (!string.IsNullOrWhiteSpace(normalized) && !normalized.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseTransport(normalized, out var parsedTransport, out error))
            {
                return false;
            }

            foreach (var target in targetResolution.Targets)
            {
                resolved[target.Name] = parsedTransport;
            }

            return true;
        }

        foreach (var target in targetResolution.Targets)
        {
            if (targetResolution.InventoryTransportPolicies is not null
                && targetResolution.InventoryTransportPolicies.TryGetValue(target.Name, out var inventoryTransport)
                && inventoryTransport is not null)
            {
                resolved[target.Name] = inventoryTransport.Value;
                continue;
            }

            resolved[target.Name] = defaultTransport ?? TransportKind.WinRm;
        }

        return true;
    }

    private static ExecutionPlan CreateHostsTestProbePlan(TargetSpec target, TransportKind transport)
    {
        var runId = $"hosts-test-{Guid.NewGuid():N}";
        var job = new DispatchJob(
            RunId: runId,
            Targets: [target],
            Payload: new CommandPayload("probe", "cmd", null),
            Transport: transport,
            ExecutionContext: new ExecutionContextOptions(),
            ScriptTransferPolicy: new ScriptTransferPolicy(string.Empty, false),
            TimeoutPolicy: new TimeoutPolicy(),
            RetryPolicy: new RetryPolicy(),
            ExpectedExitCodes: [0],
            ArtifactPolicy: new ArtifactPolicy([]),
            ResultPolicy: new ResultPolicy(string.Empty));
        var targetExecution = new TargetExecution(
            runId,
            target,
            TargetExecutionState.Probing,
            null,
            null,
            $@"C:\ProgramData\Dispatch\Runs\{runId}\script\hosts-test-probe.ps1");
        return new ExecutionPlan(
            runId,
            DateTimeOffset.UtcNow,
            job,
            [targetExecution],
            DryRun: true);
    }

    private static int RenderCredentialOperationResult(
        CredentialProviderOperationResult result,
        DispatchOutputMode outputMode)
    {
        if (outputMode is DispatchOutputMode.Json or DispatchOutputMode.Ndjson or DispatchOutputMode.Yaml)
        {
            DispatchStructuredOutputRenderer.RenderCredentialOperation(Console.Out, result, outputMode);
            return result.ProviderAvailable && result.Succeeded ? 0 : 1;
        }

        if (!result.ProviderAvailable)
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Dispatch Credentials Unavailable", result.Message);
            return 1;
        }

        if (!result.Succeeded)
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Dispatch Credentials Failed", result.Message);
            return 1;
        }

        DispatchStructuredOutputRenderer.RenderCredentialOperation(Console.Out, result, outputMode);
        return 0;
    }

    private async Task<bool> TryValidateCredentialReferenceAsync(
        DispatchRunCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.CredentialReference))
        {
            return true;
        }

        var provider = CreateCredentialProviderForCommand(command);
        var result = await provider
            .TestAsync(new CredentialReferenceRequest(command.CredentialReference.Trim()), cancellationToken)
            .ConfigureAwait(false);
        if (result.ProviderAvailable && result.Succeeded)
        {
            return true;
        }

        var title = result.ProviderAvailable
            ? "Dispatch Credential Reference Invalid"
            : "Dispatch Credentials Unavailable";
        SpectreConsoleRenderer.RenderError(Console.Error, title, result.Message);
        return false;
    }

    private ICredentialProvider CreateCredentialProviderForCommand(DispatchRunCommand command)
    {
        if (!TryCreateExplicitConfiguration(command, out var configuration, out var explicitOptions))
        {
            return credentialProvider;
        }

        if (!configuration.GetSection("Credentials").GetChildren().Any())
        {
            return credentialProvider;
        }

        return new ConfigurationCredentialProvider(configuration, Options.Create(explicitOptions), runtimeCredentialPrompt);
    }

    private IRuntimeCredentialResolver CreateRuntimeCredentialResolverForCommand(DispatchRunCommand command)
    {
        if (!TryCreateExplicitConfiguration(command, out var configuration, out var explicitOptions)
            || !configuration.GetSection("Credentials").GetChildren().Any())
        {
            return runtimeCredentialResolver;
        }

        return new ConfigurationRuntimeCredentialResolver(
            configuration,
            Options.Create(explicitOptions),
            runtimeCredentialPrompt);
    }

    private bool TryCreateExplicitConfiguration(
        DispatchRunCommand command,
        out IConfiguration configuration,
        out DispatchOptions explicitOptions)
    {
        configuration = new ConfigurationBuilder().Build();
        explicitOptions = options.Value;
        if (string.IsNullOrWhiteSpace(command.ConfigPath) || !File.Exists(command.ConfigPath))
        {
            return false;
        }

        try
        {
            configuration = DispatchConfigFileReader.Load(command.ConfigPath);
            var section = configuration.GetSection(DispatchOptions.SectionName);
            var currentOptions = options.Value;
            explicitOptions = new DispatchOptions
            {
                Inventory = currentOptions.Inventory,
                Target = currentOptions.Target,
                Exclude = currentOptions.Exclude,
                LocalRunRoot = currentOptions.LocalRunRoot,
                RemoteRunRoot = currentOptions.RemoteRunRoot,
                DefaultTransport = currentOptions.DefaultTransport,
                Throttle = currentOptions.Throttle,
                ExpectedExitCodes = currentOptions.ExpectedExitCodes,
                PsExecPath = currentOptions.PsExecPath,
                CredentialProvider = section["CredentialProvider"] ?? currentOptions.CredentialProvider,
                CredentialStorePath = section["CredentialStorePath"] ?? currentOptions.CredentialStorePath
            };
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            configuration = new ConfigurationBuilder().Build();
            explicitOptions = options.Value;
            return false;
        }
    }

    private static bool TryValidateCredentialName(string? name, out string? error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Credential name is required.";
            return false;
        }

        error = null;
        return true;
    }

    private static string? NormalizeOptionalValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private bool TryBuildPushPlan(
        string? source,
        string? destination,
        bool recurse,
        bool checksum,
        bool overwrite,
        bool backup,
        bool execute,
        bool cleanup,
        string? inventory,
        string? target,
        string? exclude,
        string? transport,
        string? credentialReference,
        int? concurrency,
        string? configPath,
        DispatchOutputMode outputMode,
        out DispatchPushPlan? plan,
        out string error)
    {
        plan = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(source))
        {
            error = "push requires <source>.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            error = "push requires --dest <remote-path>.";
            return false;
        }

        if (backup && !overwrite)
        {
            error = "--backup requires --overwrite because backup only applies when replacing an existing remote file.";
            return false;
        }

        if (concurrency is <= 0)
        {
            error = "--concurrency must be a positive integer.";
            return false;
        }

        if (concurrency is > 1)
        {
            error = "--concurrency greater than 1 is planned for push but is not implemented in this push slice.";
            return false;
        }

        var sourcePath = Path.GetFullPath(source);
        var sourceIsDirectory = Directory.Exists(sourcePath);
        if (sourceIsDirectory && !recurse)
        {
            error = "Directory push requires --recurse.";
            return false;
        }

        if (!sourceIsDirectory && !File.Exists(sourcePath))
        {
            error = $"Push source file '{sourcePath}' does not exist.";
            return false;
        }

        var destinationPath = destination.Replace('/', '\\').Trim();
        if (ContainsInvalidPushDestinationCharacter(destinationPath))
        {
            error = "Push destination must not contain control characters or invalid Windows path characters.";
            return false;
        }

        var destinationValidation = AdminSharePath.FromRemoteWindowsPath("TARGET", destinationPath);
        if (!destinationValidation.IsValid)
        {
            error = destinationValidation.Error!.Message;
            return false;
        }

        if (!TryLoadPushConfig(configPath, out var config, out error))
        {
            return false;
        }

        var targetResolution = TargetResolver.Resolve(new TargetResolutionInput(
            ComputerNameValues: [],
            TargetFile: null,
            TargetSelectors: [NormalizeOptionalValue(target) ?? config.Target ?? string.Empty],
            InventoryPath: NormalizeOptionalValue(inventory) ?? config.Inventory,
            ExcludeSelectors: (NormalizeOptionalValue(exclude) ?? config.Exclude) is { } excludeSelector ? [excludeSelector] : []));
        if (!targetResolution.IsValid)
        {
            error = string.Join(Environment.NewLine, targetResolution.Errors.Select(static item => $"{item.Code}: {item.Message}"));
            return false;
        }

        if (!TryResolvePushTransport(transport, targetResolution, config.DefaultTransport, out var resolvedTransport, out error))
        {
            return false;
        }

        if (resolvedTransport is not (TransportKind.WinRm or TransportKind.Psrp))
        {
            error = $"push currently supports raw WinRM and PSRP. Transport '{resolvedTransport.ToDispatchString()}' is planned or deferred for a later push slice.";
            return false;
        }

        var targets = targetResolution.Targets;
        if (!string.IsNullOrWhiteSpace(credentialReference))
        {
            targets = targets
                .Select(targetSpec => targetSpec with { CredentialReference = credentialReference.Trim() })
                .ToArray();
        }

        var sourceFiles = sourceIsDirectory
            ? EnumeratePushSourceFiles(sourcePath)
            : [sourcePath];
        if (sourceFiles.Count == 0)
        {
            error = $"Push source directory '{sourcePath}' contains no files to upload.";
            return false;
        }

        if (execute && sourceIsDirectory)
        {
            error = "--execute currently supports single-file PowerShell script pushes only; directory execute remains later push work.";
            return false;
        }

        if (execute && !string.Equals(Path.GetExtension(sourcePath), ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            error = "--execute currently supports pushed PowerShell script files with a .ps1 extension.";
            return false;
        }

        if (cleanup && !execute)
        {
            error = "--cleanup requires --execute so Dispatch only removes a pushed script after it has run.";
            return false;
        }

        foreach (var sourceFile in sourceFiles)
        {
            if (new FileInfo(sourceFile).Length > int.MaxValue)
            {
                error = $"Push source file '{sourceFile}' is too large for the current WinRM chunked upload path.";
                return false;
            }
        }

        var sourceBytes = sourceFiles.Sum(static item => new FileInfo(item).Length);
        if (sourceBytes < 0)
        {
            error = $"Push source '{sourcePath}' is too large.";
            return false;
        }

        plan = new DispatchPushPlan(
            Mode: "push",
            SourcePath: sourcePath,
            DestinationPath: destinationPath,
            SourceBytes: sourceBytes,
            Transport: resolvedTransport,
            Targets: targets,
            Overwrite: overwrite,
            Checksum: checksum,
            Backup: backup,
            Execute: execute,
            Cleanup: cleanup,
            Concurrency: 1,
            OutputMode: outputMode);
        return true;
    }

    private async Task<DispatchPushResult> ExecutePushPlanAsync(
        DispatchPushPlan plan,
        bool noProgress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var pushItems = await CreatePushTransferItemsAsync(plan, cancellationToken).ConfigureAwait(false);
        var credentials = await ResolvePushCredentialsAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!credentials.Succeeded)
        {
            return new DispatchPushResult(
                plan,
                false,
                startedAt,
                DateTimeOffset.UtcNow,
                [
                    new DispatchPushTargetResult(
                        Target: "-",
                        Succeeded: false,
                        FailureCategory: FailureCategory.AuthenticationFailed,
                        FailureMessage: credentials.FailureMessage ?? "Credential resolution failed.",
                        BytesUploaded: 0,
                        Metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["pushFileCount"] = pushItems.Count.ToString()
                        })
                ]);
        }

        try
        {
            var targetResults = new List<DispatchPushTargetResult>();
            foreach (var target in plan.Targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var credential = target.CredentialReference is not null
                    && credentials.Credentials.TryGetValue(target.CredentialReference, out var resolvedCredential)
                        ? resolvedCredential
                        : null;

                var bytesUploaded = 0L;
                var uploadMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pushFileCount"] = pushItems.Count.ToString(),
                    ["checksumRequested"] = plan.Checksum.ToString(),
                    ["backupRequested"] = plan.Backup.ToString(),
                    ["executeRequested"] = plan.Execute.ToString(),
                    ["cleanupRequested"] = plan.Cleanup.ToString()
                };
                PushTargetFailure? targetFailure = null;
                var checksumVerifiedCount = 0;
                var backupCreatedCount = 0;
                foreach (var item in pushItems)
                {
                    var upload = await UploadPushItemAsync(plan, target.Name, item, credential, cancellationToken).ConfigureAwait(false);

                    if (upload.Metadata is not null)
                    {
                        foreach (var pair in upload.Metadata)
                        {
                            uploadMetadata[pair.Key] = pair.Value;
                        }
                    }

                    if (!upload.Succeeded)
                    {
                        uploadMetadata["pushFailedSourcePath"] = item.SourcePath;
                        uploadMetadata["pushFailedRemotePath"] = item.RemotePath;
                        targetFailure = new PushTargetFailure(upload.FailureCategory, upload.FailureMessage, ResetBytesUploaded: true);
                        break;
                    }

                    if (plan.Checksum
                        && !TryVerifyPushChecksum(item, upload.Metadata, out var checksumFailure))
                    {
                        uploadMetadata["checksumStage"] = "verify";
                        uploadMetadata["checksumFailedSourcePath"] = item.SourcePath;
                        uploadMetadata["checksumFailedRemotePath"] = item.RemotePath;
                        targetFailure = new PushTargetFailure(FailureCategory.ScriptTransferFailed, checksumFailure, ResetBytesUploaded: true);
                        break;
                    }

                    if (plan.Checksum)
                    {
                        checksumVerifiedCount++;
                    }

                    if (plan.Backup
                        && upload.Metadata is not null
                        && upload.Metadata.TryGetValue("uploadBackupCreated", out var backupCreated)
                        && bool.TryParse(backupCreated, out var didCreateBackup)
                        && didCreateBackup)
                    {
                        backupCreatedCount++;
                    }

                    bytesUploaded += item.TransferPlan.TotalBytes;
                }

                if (targetFailure is null && plan.Execute)
                {
                    var execution = await ExecutePushedScriptAsync(plan, target.Name, pushItems[0], credential, cancellationToken).ConfigureAwait(false);
                    foreach (var pair in execution.Metadata)
                    {
                        uploadMetadata[pair.Key] = pair.Value;
                    }

                    if (!execution.Succeeded)
                    {
                        targetFailure = new PushTargetFailure(execution.FailureCategory, execution.FailureMessage, ResetBytesUploaded: false);
                    }
                }

                if (targetFailure is null && plan.Cleanup)
                {
                    var cleanup = await CleanupPushedScriptAsync(plan, target.Name, pushItems[0], credential, cancellationToken).ConfigureAwait(false);
                    foreach (var pair in cleanup.Metadata)
                    {
                        uploadMetadata[pair.Key] = pair.Value;
                    }

                    if (!cleanup.Succeeded)
                    {
                        targetFailure = new PushTargetFailure(cleanup.FailureCategory, cleanup.FailureMessage, ResetBytesUploaded: false);
                    }
                }

                if (plan.Checksum)
                {
                    uploadMetadata["checksumMode"] = "sha256";
                    uploadMetadata["checksumVerifiedFileCount"] = checksumVerifiedCount.ToString();
                }

                if (plan.Backup)
                {
                    uploadMetadata["backupCreatedFileCount"] = backupCreatedCount.ToString();
                }

                targetResults.Add(new DispatchPushTargetResult(
                    target.Name,
                    targetFailure is null,
                    targetFailure?.FailureCategory ?? FailureCategory.None,
                    targetFailure?.FailureMessage,
                    targetFailure is { ResetBytesUploaded: true } ? 0 : bytesUploaded,
                    uploadMetadata));
            }

            return new DispatchPushResult(
                plan,
                targetResults.All(static target => target.Succeeded),
                startedAt,
                DateTimeOffset.UtcNow,
                targetResults);
        }
        finally
        {
            foreach (var credential in credentials.Credentials.Values)
            {
                credential.Dispose();
            }
        }
    }

    private async Task<PushUploadResult> UploadPushItemAsync(
        DispatchPushPlan plan,
        string targetName,
        DispatchPushTransferItem item,
        DispatchResolvedCredential? credential,
        CancellationToken cancellationToken)
    {
        if (plan.Transport == TransportKind.WinRm)
        {
            if (winRmTransferClient is null)
            {
                return PushUploadResult.Failed(
                    FailureCategory.TransportUnavailable,
                    "Raw WinRM push transfer is unavailable because the raw WinRM transfer client is not registered.",
                    null);
            }

            var upload = await winRmTransferClient.UploadAsync(
                    new WinRmScriptTransferRequest(
                        targetName,
                        item.RemotePath,
                        item.TransferPlan,
                        ProgressReporter: null,
                        Credential: credential,
                        Overwrite: plan.Overwrite,
                        Backup: plan.Backup),
                    cancellationToken)
                .ConfigureAwait(false);
            return new PushUploadResult(upload.Succeeded, upload.FailureCategory, upload.FailureMessage, upload.Metadata);
        }

        if (psrpFileTransferClient is null)
        {
            return PushUploadResult.Failed(
                FailureCategory.TransportUnavailable,
                "PSRP push transfer is unavailable because the PSRP transfer client is not registered.",
                null);
        }

        var psrpUpload = await psrpFileTransferClient.UploadAsync(
                new PsrpFileTransferRequest(
                    targetName,
                    item.RemotePath,
                    item.TransferPlan,
                    ProgressReporter: null,
                    Credential: credential,
                    Overwrite: plan.Overwrite,
                    Backup: plan.Backup),
                cancellationToken)
            .ConfigureAwait(false);
        return new PushUploadResult(psrpUpload.Succeeded, psrpUpload.FailureCategory, psrpUpload.FailureMessage, psrpUpload.Metadata);
    }

    private async Task<PushExecutionResult> ExecutePushedScriptAsync(
        DispatchPushPlan plan,
        string targetName,
        DispatchPushTransferItem item,
        DispatchResolvedCredential? credential,
        CancellationToken cancellationToken)
    {
        var command = CreatePushedPowerShellCommand(item.RemotePath);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["executionRequested"] = "True",
            ["executionRemotePath"] = item.RemotePath,
            ["executionTransport"] = plan.Transport.ToDispatchString(),
            ["executionCommand"] = command.RenderedCommand,
            ["executionStatus"] = "pending"
        };

        if (plan.Transport == TransportKind.WinRm)
        {
            if (winRmShellClient is null)
            {
                return PushExecutionResult.Failed(
                    FailureCategory.TransportUnavailable,
                    "Raw WinRM push execute is unavailable because the raw WinRM shell client is not registered.",
                    metadata);
            }

            var result = await winRmShellClient.ExecuteAsync(
                    new WinRmShellCommandRequest(
                        targetName,
                        command.Executable,
                        command.Arguments,
                        [],
                        Credential: credential),
                    cancellationToken)
                .ConfigureAwait(false);
            MergeMetadata(metadata, result.Metadata);
            return CreatePushExecutionResult(metadata, result.Succeeded, result.ExitCode, result.Stdout, result.Stderr, result.FailureCategory, result.FailureMessage, result.TimedOut);
        }

        if (psrpCommandClient is null)
        {
            return PushExecutionResult.Failed(
                FailureCategory.TransportUnavailable,
                "PSRP push execute is unavailable because the PSRP command client is not registered.",
                metadata);
        }

        var psrpResult = await psrpCommandClient.ExecuteAsync(
                new PsrpCommandRequest(
                    targetName,
                    command.Executable,
                    RenderCommandArguments(command.Arguments),
                    WorkingDirectory: null,
                    ExecutionTimeout: null,
                    ConfigurationName: null,
                    PsrpConnectionKind.WsMan,
                    PsrpAuthenticationKind.Default,
                    CertificateThumbprint: null,
                    Credential: credential),
                cancellationToken)
            .ConfigureAwait(false);
        MergeMetadata(metadata, psrpResult.Metadata);
        return CreatePushExecutionResult(metadata, psrpResult.Succeeded, psrpResult.ExitCode, psrpResult.Stdout, psrpResult.Stderr, psrpResult.FailureCategory, psrpResult.FailureMessage, timedOut: false);
    }

    private static DirectExecutionCommand CreatePushedPowerShellCommand(string remotePath) =>
        new("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", remotePath]);

    private async Task<PushCleanupResult> CleanupPushedScriptAsync(
        DispatchPushPlan plan,
        string targetName,
        DispatchPushTransferItem item,
        DispatchResolvedCredential? credential,
        CancellationToken cancellationToken)
    {
        var command = CreatePushedCleanupCommand(item.RemotePath);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cleanupRequested"] = "True",
            ["cleanupRemotePath"] = item.RemotePath,
            ["cleanupTransport"] = plan.Transport.ToDispatchString(),
            ["cleanupCommand"] = command.RenderedCommand,
            ["cleanupStatus"] = "pending"
        };

        if (plan.Transport == TransportKind.WinRm)
        {
            if (winRmShellClient is null)
            {
                return PushCleanupResult.Failed(
                    "Raw WinRM push cleanup is unavailable because the raw WinRM shell client is not registered.",
                    metadata);
            }

            var result = await winRmShellClient.ExecuteAsync(
                    new WinRmShellCommandRequest(
                        targetName,
                        command.Executable,
                        command.Arguments,
                        [],
                        Credential: credential),
                    cancellationToken)
                .ConfigureAwait(false);
            MergeMetadata(metadata, result.Metadata);
            return CreatePushCleanupResult(metadata, result.Succeeded, result.ExitCode, result.Stdout, result.Stderr, result.FailureCategory, result.FailureMessage, result.TimedOut);
        }

        if (psrpCommandClient is null)
        {
            return PushCleanupResult.Failed(
                "PSRP push cleanup is unavailable because the PSRP command client is not registered.",
                metadata);
        }

        var psrpResult = await psrpCommandClient.ExecuteAsync(
                new PsrpCommandRequest(
                    targetName,
                    command.Executable,
                    RenderCommandArguments(command.Arguments),
                    WorkingDirectory: null,
                    ExecutionTimeout: null,
                    ConfigurationName: null,
                    PsrpConnectionKind.WsMan,
                    PsrpAuthenticationKind.Default,
                    CertificateThumbprint: null,
                    Credential: credential),
                cancellationToken)
            .ConfigureAwait(false);
        MergeMetadata(metadata, psrpResult.Metadata);
        return CreatePushCleanupResult(metadata, psrpResult.Succeeded, psrpResult.ExitCode, psrpResult.Stdout, psrpResult.Stderr, psrpResult.FailureCategory, psrpResult.FailureMessage, timedOut: false);
    }

    private static DirectExecutionCommand CreatePushedCleanupCommand(string remotePath) =>
        new(
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $"Remove-Item -LiteralPath {QuotePowerShellSingleQuotedString(remotePath)} -Force -ErrorAction Stop"]);

    private static string QuotePowerShellSingleQuotedString(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static PushExecutionResult CreatePushExecutionResult(
        Dictionary<string, string> metadata,
        bool transportSucceeded,
        int? exitCode,
        string stdout,
        string stderr,
        FailureCategory failureCategory,
        string? failureMessage,
        bool timedOut)
    {
        if (exitCode is not null)
        {
            metadata["executionExitCode"] = exitCode.Value.ToString();
        }

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            metadata["executionStdout"] = stdout;
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            metadata["executionStderr"] = stderr;
        }

        if (!transportSucceeded)
        {
            var category = timedOut
                ? FailureCategory.TimedOut
                : failureCategory == FailureCategory.None
                    ? FailureCategory.ExecutionFailed
                    : failureCategory;
            metadata["executionStatus"] = timedOut ? "timed-out" : "failed";
            metadata["executionFailureCategory"] = category.ToString();
            return PushExecutionResult.Failed(
                category,
                failureMessage ?? "Push execute failed after upload.",
                metadata);
        }

        if (exitCode is not 0)
        {
            metadata["executionStatus"] = "failed";
            metadata["executionFailureCategory"] = FailureCategory.UnexpectedExitCode.ToString();
            return PushExecutionResult.Failed(
                FailureCategory.UnexpectedExitCode,
                $"Push execute exited with code {exitCode}; expected 0.",
                metadata);
        }

        metadata["executionStatus"] = "completed";
        metadata["executionFailureCategory"] = FailureCategory.None.ToString();
        return PushExecutionResult.Success(metadata);
    }

    private static PushCleanupResult CreatePushCleanupResult(
        Dictionary<string, string> metadata,
        bool transportSucceeded,
        int? exitCode,
        string stdout,
        string stderr,
        FailureCategory failureCategory,
        string? failureMessage,
        bool timedOut)
    {
        if (exitCode is not null)
        {
            metadata["cleanupExitCode"] = exitCode.Value.ToString();
        }

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            metadata["cleanupStdout"] = stdout;
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            metadata["cleanupStderr"] = stderr;
        }

        if (!transportSucceeded || exitCode is not 0)
        {
            metadata["cleanupStatus"] = timedOut ? "timed-out" : "failed";
            metadata["cleanupFailureCategory"] = FailureCategory.CleanupFailed.ToString();
            var message = failureMessage
                ?? (exitCode is not null
                    ? $"Push cleanup exited with code {exitCode}; expected 0."
                    : "Push cleanup failed after upload.");
            return PushCleanupResult.Failed(message, metadata);
        }

        metadata["cleanupStatus"] = "completed";
        metadata["cleanupFailureCategory"] = FailureCategory.None.ToString();
        return PushCleanupResult.Success(metadata);
    }

    private static void MergeMetadata(IDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? additionalMetadata)
    {
        if (additionalMetadata is null)
        {
            return;
        }

        foreach (var pair in additionalMetadata)
        {
            metadata[pair.Key] = pair.Value;
        }
    }

    private static string RenderCommandArguments(IReadOnlyList<string> arguments) =>
        string.Join(" ", arguments.Select(QuoteCommandArgumentIfNeeded));

    private static string QuoteCommandArgumentIfNeeded(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static bool TryVerifyPushChecksum(
        DispatchPushTransferItem item,
        IReadOnlyDictionary<string, string>? metadata,
        out string failure)
    {
        failure = string.Empty;
        if (metadata is null)
        {
            failure = $"Push checksum comparison did not receive SHA-256 metadata for '{item.RemotePath}'.";
            return false;
        }

        if (!metadata.TryGetValue("uploadExpectedSha256", out var expected)
            || string.IsNullOrWhiteSpace(expected))
        {
            failure = $"Push checksum comparison did not receive the expected SHA-256 for '{item.RemotePath}'.";
            return false;
        }

        if (!string.Equals(expected, item.TransferPlan.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            failure = $"Push checksum comparison expected SHA-256 '{item.TransferPlan.ContentSha256}' for '{item.RemotePath}', but the transport reported expected SHA-256 '{expected}'.";
            return false;
        }

        if (!metadata.TryGetValue("uploadReportedSha256", out var reported)
            || string.IsNullOrWhiteSpace(reported))
        {
            failure = $"Push checksum comparison did not receive the remote SHA-256 for '{item.RemotePath}'.";
            return false;
        }

        if (!string.Equals(reported, item.TransferPlan.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            failure = $"Push checksum comparison failed for '{item.RemotePath}': remote SHA-256 '{reported}' did not match local SHA-256 '{item.TransferPlan.ContentSha256}'.";
            return false;
        }

        return true;
    }

    private async Task<RuntimeCredentialResolutionResult> ResolvePushCredentialsAsync(
        DispatchPushPlan plan,
        CancellationToken cancellationToken)
    {
        var references = plan.Targets
            .Select(static target => target.CredentialReference)
            .Where(static reference => !string.IsNullOrWhiteSpace(reference))
            .Select(static reference => reference!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return references.Length == 0
            ? RuntimeCredentialResolutionResult.Success(new Dictionary<string, DispatchResolvedCredential>(StringComparer.OrdinalIgnoreCase))
            : await runtimeCredentialResolver.ResolveAsync(references, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<DispatchPushTransferItem>> CreatePushTransferItemsAsync(
        DispatchPushPlan plan,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(plan.SourcePath))
        {
            return
            [
                new DispatchPushTransferItem(
                    plan.SourcePath,
                    plan.DestinationPath,
                    await CreatePushTransferPlanAsync(plan.SourcePath, cancellationToken).ConfigureAwait(false))
            ];
        }

        var items = new List<DispatchPushTransferItem>();
        foreach (var sourceFile in EnumeratePushSourceFiles(plan.SourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(plan.SourcePath, sourceFile)
                .Replace(Path.DirectorySeparatorChar, '\\')
                .Replace(Path.AltDirectorySeparatorChar, '\\');
            items.Add(new DispatchPushTransferItem(
                sourceFile,
                CombineRemotePushPath(plan.DestinationPath, relativePath),
                await CreatePushTransferPlanAsync(sourceFile, cancellationToken).ConfigureAwait(false)));
        }

        return items;
    }

    private static async Task<ScriptTransferPlan> CreatePushTransferPlanAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        const int chunkSizeBytes = 8192;
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var chunks = new List<ScriptTransferChunk>((bytes.Length + chunkSizeBytes - 1) / chunkSizeBytes);
        for (var offset = 0; offset < bytes.Length; offset += chunkSizeBytes)
        {
            var chunkLength = Math.Min(chunkSizeBytes, bytes.Length - offset);
            var chunkBytes = bytes.AsSpan(offset, chunkLength).ToArray();
            chunks.Add(new ScriptTransferChunk(
                chunks.Count,
                offset,
                chunkLength,
                ComputeSha256(chunkBytes),
                Convert.ToBase64String(chunkBytes)));
        }

        return new ScriptTransferPlan(
            ScriptTransferMode.WinRmChunkedBase64,
            bytes.Length,
            ComputeSha256(bytes),
            chunkSizeBytes,
            chunks);
    }

    private static string ComputeSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static IReadOnlyList<string> EnumeratePushSourceFiles(string sourceDirectory) =>
        Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string CombineRemotePushPath(string remoteRoot, string relativePath)
    {
        var normalizedRoot = remoteRoot.Replace('/', '\\').TrimEnd('\\');
        if (normalizedRoot.EndsWith(":", StringComparison.Ordinal))
        {
            normalizedRoot += "\\";
        }

        return normalizedRoot.EndsWith('\\')
            ? normalizedRoot + relativePath
            : normalizedRoot + "\\" + relativePath;
    }

    private static bool ContainsInvalidPushDestinationCharacter(string path)
    {
        var invalid = Path.GetInvalidPathChars();
        return path.Any(character => char.IsControl(character) || invalid.Contains(character));
    }

    private bool TryLoadPushConfig(
        string? configPath,
        out PushCommandConfig config,
        out string error)
    {
        config = new PushCommandConfig(options.Value.Inventory, options.Value.Target, options.Value.Exclude, options.Value.DefaultTransport);
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return true;
        }

        if (!File.Exists(configPath))
        {
            error = $"Config file '{configPath}' does not exist.";
            return false;
        }

        try
        {
            var configuration = DispatchConfigFileReader.Load(configPath);
            var section = configuration.GetSection(DispatchOptions.SectionName);
            var defaultTransport = config.DefaultTransport;
            if (section["DefaultTransport"] is { Length: > 0 } configuredTransport)
            {
                if (!TryParseTransport(configuredTransport, out var parsedTransport, out error))
                {
                    return false;
                }

                defaultTransport = parsedTransport;
            }

            config = config with
            {
                Inventory = section["Inventory"] ?? config.Inventory,
                Target = section["Target"] ?? config.Target,
                Exclude = section["Exclude"] ?? config.Exclude,
                DefaultTransport = defaultTransport
            };
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            error = $"Config file '{configPath}' could not be read: {exception.Message}";
            return false;
        }
    }

    private static bool TryResolvePushTransport(
        string? transport,
        TargetResolutionResult targetResolution,
        TransportKind? defaultTransport,
        out TransportKind resolvedTransport,
        out string error)
    {
        resolvedTransport = TransportKind.WinRm;
        error = string.Empty;
        var normalized = NormalizeOptionalValue(transport);
        if (!string.IsNullOrWhiteSpace(normalized) && !normalized.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseTransport(normalized, out resolvedTransport, out error);
        }

        if (targetResolution.InventoryTransport is not null)
        {
            resolvedTransport = targetResolution.InventoryTransport.Value;
            return true;
        }

        if (defaultTransport is not null)
        {
            resolvedTransport = defaultTransport.Value;
            return true;
        }

        resolvedTransport = TransportKind.WinRm;
        return true;
    }

    private static bool TryParseTransport(string value, out TransportKind transport, out string error)
    {
        error = string.Empty;
        switch (value.Trim().ToLowerInvariant())
        {
            case "psexec":
                transport = TransportKind.PsExec;
                return true;
            case "psrp":
                transport = TransportKind.Psrp;
                return true;
            case "winrm":
                transport = TransportKind.WinRm;
                return true;
            default:
                transport = TransportKind.WinRm;
                error = $"Unsupported transport '{value}'.";
                return false;
        }
    }

    private static IReadOnlyList<DispatchInitTemplate> GetInitTemplates(DispatchInitScaffold scaffold) =>
        scaffold switch
        {
            DispatchInitScaffold.Config => [ConfigTemplate],
            DispatchInitScaffold.Hosts => [HostsTemplate],
            DispatchInitScaffold.Job => [JobTemplate],
            DispatchInitScaffold.All => [ConfigTemplate, HostsTemplate, JobTemplate],
            _ => throw new ArgumentOutOfRangeException(nameof(scaffold), scaffold, "Unsupported init scaffold.")
        };

    private static readonly DispatchInitTemplate ConfigTemplate = new(
        "config.yml",
        """
        dispatch:
          default_transport: psrp
          inventory: hosts.yml
          target: workstations
          local_run_root: C:\ProgramData\Dispatch\Runs
          remote_run_root: C:\ProgramData\Dispatch
          throttle: 4

        credentials:
          prod-admin:
            provider: prompt
            username: CONTOSO\prod.admin
        """);

    private static readonly DispatchInitTemplate HostsTemplate = new(
        "hosts.yml",
        """
        defaults:
          transport: psrp
          credential: prod-admin

        groups:
          workstations:
            hosts: [PC001, PC002]

        hosts:
          PC001:
          PC002:
        """);

    private static readonly DispatchInitTemplate JobTemplate = new(
        "job.yml",
        """
        name: Starter Dispatch job
        hosts: workstations
        transport: psrp
        defaults:
          expected_exit_codes: [0]
        strategy:
          serial: 2
        tasks:
          - cmd: whoami
        """);

    private static int RenderUnknownCommand(string command)
    {
        SpectreConsoleRenderer.RenderError(
            Console.Error,
            "Unknown Dispatch Command",
            $"'{command}' is not a Dispatch command.");
        return 1;
    }

    private static bool TryParseOutputMode(string? value, out DispatchOutputMode outputMode, out string? error)
    {
        outputMode = DispatchOutputMode.Rich;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        outputMode = value.Trim().ToLowerInvariant() switch
        {
            "rich" => DispatchOutputMode.Rich,
            "table" => DispatchOutputMode.Table,
            "json" => DispatchOutputMode.Json,
            "ndjson" => DispatchOutputMode.Ndjson,
            "yaml" => DispatchOutputMode.Yaml,
            _ => default
        };

        if (value.Trim().Equals("rich", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("table", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("json", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("ndjson", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("yaml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        error = $"Unsupported output mode '{value}'. Expected rich, table, json, ndjson, or yaml.";
        return false;
    }

}

internal enum DispatchInitScaffold
{
    Config,
    Hosts,
    Job,
    All
}

internal sealed record DispatchInitTemplate(string FileName, string Content)
{
    public string Path { get; init; } = string.Empty;
}

internal sealed record PushCommandConfig(
    string? Inventory,
    string? Target,
    string? Exclude,
    TransportKind? DefaultTransport);

internal sealed record DispatchPushTransferItem(
    string SourcePath,
    string RemotePath,
    ScriptTransferPlan TransferPlan);

internal sealed record PushUploadResult(
    bool Succeeded,
    FailureCategory FailureCategory,
    string? FailureMessage,
    IReadOnlyDictionary<string, string>? Metadata)
{
    public static PushUploadResult Failed(
        FailureCategory failureCategory,
        string failureMessage,
        IReadOnlyDictionary<string, string>? metadata) =>
        new(false, failureCategory, failureMessage, metadata);
}

internal sealed record PushExecutionResult(
    bool Succeeded,
    FailureCategory FailureCategory,
    string? FailureMessage,
    IReadOnlyDictionary<string, string> Metadata)
{
    public static PushExecutionResult Success(IReadOnlyDictionary<string, string> metadata) =>
        new(true, FailureCategory.None, null, metadata);

    public static PushExecutionResult Failed(
        FailureCategory failureCategory,
        string failureMessage,
        IReadOnlyDictionary<string, string> metadata) =>
        new(false, failureCategory, failureMessage, metadata);
}

internal sealed record PushCleanupResult(
    bool Succeeded,
    FailureCategory FailureCategory,
    string? FailureMessage,
    IReadOnlyDictionary<string, string> Metadata)
{
    public static PushCleanupResult Success(IReadOnlyDictionary<string, string> metadata) =>
        new(true, FailureCategory.None, null, metadata);

    public static PushCleanupResult Failed(
        string failureMessage,
        IReadOnlyDictionary<string, string> metadata) =>
        new(false, FailureCategory.CleanupFailed, failureMessage, metadata);
}

internal sealed record PushTargetFailure(
    FailureCategory FailureCategory,
    string? FailureMessage,
    bool ResetBytesUploaded);
