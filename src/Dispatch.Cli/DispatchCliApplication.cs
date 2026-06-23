using Dispatch.Core;
using Dispatch.Core.Configuration;
using Dispatch.Core.Credentials;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Dispatch.Transports.PsExec;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
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
    IRuntimeCredentialPrompt? runtimeCredentialPrompt = null)
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
        try
        {
            if (!await TryValidateCredentialReferenceAsync(command, cancellationToken).ConfigureAwait(false))
            {
                return 1;
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
                    return 0;
                }

                var dryRunPlan = command.OutputMode == DispatchOutputMode.Rich && !command.Quiet
                    ? await CreatePlanWithDryRunProgressAsync(request, command.NoColor, cancellationToken).ConfigureAwait(false)
                    : await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
                if (!ShouldSuppressOutput(command))
                {
                    DispatchStructuredOutputRenderer.RenderPlan(Console.Out, dryRunPlan, command.OutputMode);
                }

                return 0;
            }

            if (command.OutputMode == DispatchOutputMode.Ndjson)
            {
                var streamWriter = new DispatchNdjsonStreamWriter(Console.Out, command.Verbose, command.Trace);
                streamWriter.WritePlanningStarted();
                var streamPlan = await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
                streamWriter.WritePlan(streamPlan);
                var resolvedStreamPlan = await ResolveRuntimeCredentialsAsync(streamPlan, command, cancellationToken).ConfigureAwait(false);
                if (resolvedStreamPlan is null)
                {
                    return 1;
                }

                try
                {
                    streamWriter.WriteExecutionStarted(resolvedStreamPlan);
                    var streamResult = await executor.ExecuteAsync(resolvedStreamPlan, streamWriter, cancellationToken).ConfigureAwait(false);
                    streamWriter.WriteResult(streamResult);
                    return streamResult.FailedCount == 0 && streamResult.TimedOutCount == 0 && streamResult.CancelledCount == 0 ? 0 : 1;
                }
                finally
                {
                    DisposeRuntimeCredentials(resolvedStreamPlan);
                }
            }

            var plan = command.OutputMode == DispatchOutputMode.Rich && !command.Quiet
                ? await CreatePlanWithStatusAsync(request, command.NoColor, cancellationToken).ConfigureAwait(false)
                : await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
            var resolvedPlan = await ResolveRuntimeCredentialsAsync(plan, command, cancellationToken).ConfigureAwait(false);
            if (resolvedPlan is null)
            {
                return 1;
            }

            try
            {
                var result = command.OutputMode == DispatchOutputMode.Rich && !command.Quiet
                    ? await RunWithSpectreProgressAsync(resolvedPlan, command.NoDashboard, command.NoColor, cancellationToken).ConfigureAwait(false)
                    : await executor.ExecuteAsync(resolvedPlan, NullDispatchExecutionObserver.Instance, cancellationToken).ConfigureAwait(false);
                if (!ShouldSuppressOutput(command))
                {
                    DispatchStructuredOutputRenderer.RenderRunResult(Console.Out, result, command.OutputMode);
                }

                return result.FailedCount == 0 && result.TimedOutCount == 0 && result.CancelledCount == 0 ? 0 : 1;
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

            return 1;
        }
    }

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
        int? concurrency,
        string? outputValue,
        bool noColor,
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
                    Concurrency: concurrency,
                    OutputMode: outputMode,
                    NoColor: noColor),
                new DispatchRunCommandParser.DispatchRunAmbientConfig(
                    options.Value.Inventory,
                    options.Value.Target,
                    options.Value.Exclude,
                    options.Value.DefaultTransport),
                options.Value.ExpectedExitCodes,
                out var command,
                out var error))
        {
            SpectreConsoleRenderer.RenderError(Console.Error, "Invalid Dispatch Job", error);
            return 1;
        }

        return await RunParsedCommandAsync(command!, cancellationToken).ConfigureAwait(false);
    }

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
