using Dispatch.Core.Models;
using Dispatch.Core.Transports;

namespace Dispatch.Core.Execution;

internal sealed class DispatchExecutor(
    IScriptPreparationService scriptPreparationService,
    IDispatchArtifactCollector artifactCollector,
    IEnumerable<ITransportScriptExecutor> transportExecutors,
    IEnumerable<ITransportEndpointProbe> endpointProbes,
    IDispatchResultWriter resultWriter,
    ISystemClock clock) : IDispatchExecutor
{
    public Task<DispatchRunResult> ExecuteAsync(ExecutionPlan plan, CancellationToken cancellationToken) =>
        ExecuteAsync(plan, NullDispatchExecutionObserver.Instance, cancellationToken);

    public async Task<DispatchRunResult> ExecuteAsync(
        ExecutionPlan plan,
        IDispatchExecutionObserver observer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(observer);

        if (plan.DryRun)
        {
            throw new InvalidOperationException("Dry-run plans cannot be executed.");
        }

        var startedAt = clock.UtcNow;
        await using var eventStreamWriter = CreateEventStreamWriter(plan);
        eventStreamWriter?.WriteRunStarted(plan, startedAt);
        eventStreamWriter?.WritePlan(plan);
        eventStreamWriter?.WriteExecutionStarted(plan, startedAt);

        var effectiveObserver = eventStreamWriter is null
            ? observer
            : observer is NullDispatchExecutionObserver
                ? eventStreamWriter
                : new CompositeDispatchExecutionObserver(observer, eventStreamWriter);

        var transportExecutor = transportExecutors.SingleOrDefault(executor => executor.Kind == plan.Job.Transport);

        if (transportExecutor is null)
        {
            var unavailableEndedAt = clock.UtcNow;
            var unavailableTargets = new List<TargetExecutionResult>();
            foreach (var target in plan.Targets)
            {
                var failed = CreateFailureResult(
                    plan,
                    target,
                    startedAt,
                    unavailableEndedAt,
                    FailureCategory.TransportUnavailable,
                    $"No executor is registered for transport '{plan.Job.Transport.ToDispatchString()}'.");
                await NotifyTargetStateAsync(effectiveObserver, failed, unavailableEndedAt, cancellationToken).ConfigureAwait(false);
                unavailableTargets.Add(failed);
            }

            var unavailableResult = new DispatchRunResult(
                RunId: plan.RunId,
                StartedAt: startedAt,
                EndedAt: unavailableEndedAt,
                RequestedBy: Environment.UserName,
                Transport: plan.Job.Transport,
                PayloadType: plan.Job.Payload.PayloadType,
                PayloadName: plan.Job.Payload.DisplayName,
                Targets: unavailableTargets,
                ResultPath: plan.LocalResultsJsonPath);

            WriteFinalEvents(eventStreamWriter, unavailableResult);
            await resultWriter.WriteAsync(plan, unavailableResult, cancellationToken).ConfigureAwait(false);
            return unavailableResult;
        }

        var endpointProbe = endpointProbes.SingleOrDefault(probe => probe.Kind == plan.Job.Transport);
        var targetResults = await ExecuteTargetsAsync(plan, transportExecutor, endpointProbe, effectiveObserver, cancellationToken).ConfigureAwait(false);

        var endedAt = clock.UtcNow;
        var result = new DispatchRunResult(
            RunId: plan.RunId,
            StartedAt: startedAt,
            EndedAt: endedAt,
            RequestedBy: Environment.UserName,
            Transport: plan.Job.Transport,
            PayloadType: plan.Job.Payload.PayloadType,
            PayloadName: plan.Job.Payload.DisplayName,
            Targets: targetResults,
            ResultPath: plan.LocalResultsJsonPath);

        WriteFinalEvents(eventStreamWriter, result);
        await resultWriter.WriteAsync(plan, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<IReadOnlyList<TargetExecutionResult>> ExecuteTargetsAsync(
        ExecutionPlan plan,
        ITransportScriptExecutor transportExecutor,
        ITransportEndpointProbe? endpointProbe,
        IDispatchExecutionObserver observer,
        CancellationToken cancellationToken)
    {
        var throttleLimit = Math.Max(1, plan.ThrottleLimit);
        using var semaphore = new SemaphoreSlim(throttleLimit, throttleLimit);
        var tasks = plan.Targets.Select(async (target, index) =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await ExecuteTargetAsync(plan, target, transportExecutor, endpointProbe, observer, cancellationToken).ConfigureAwait(false);
                return (Index: index, Result: result);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var completed = await Task.WhenAll(tasks).ConfigureAwait(false);
        return completed
            .OrderBy(static target => target.Index)
            .Select(static target => target.Result)
            .ToArray();
    }

    private async Task<TargetExecutionResult> ExecuteTargetAsync(
        ExecutionPlan plan,
        TargetExecution target,
        ITransportScriptExecutor transportExecutor,
        ITransportEndpointProbe? endpointProbe,
        IDispatchExecutionObserver observer,
        CancellationToken cancellationToken)
    {
        try
        {
            if (endpointProbe is not null)
            {
                await NotifyTargetStateAsync(plan, target, TargetExecutionState.Probing, observer, cancellationToken).ConfigureAwait(false);
                var probeResult = await endpointProbe.ProbeAsync(new TransportEndpointProbeRequest(plan, target), cancellationToken).ConfigureAwait(false);
                if (!probeResult.Succeeded)
                {
                    var failure = CreateFailureResult(
                        plan,
                        target,
                        probeResult.StartedAt,
                        probeResult.EndedAt,
                        probeResult.FailureCategory,
                        probeResult.FailureMessage ?? $"Endpoint probe failed for target '{target.Target.Name}'.",
                        probeResult.Metadata);
                    await NotifyTargetStateAsync(observer, failure, probeResult.EndedAt, cancellationToken).ConfigureAwait(false);
                    return failure;
                }
            }

            var targetPreparation = await PrepareTargetAsync(plan, target, observer, cancellationToken).ConfigureAwait(false);
            if (!targetPreparation.Succeeded)
            {
                var failure = CreateFailureResult(
                    plan,
                    target,
                    clock.UtcNow,
                    clock.UtcNow,
                    targetPreparation.FailureCategory,
                    targetPreparation.FailureMessage ?? $"Script preparation failed for target '{target.Target.Name}'.");
                await NotifyTargetStateAsync(observer, failure, clock.UtcNow, cancellationToken).ConfigureAwait(false);
                return failure;
            }

            await NotifyTargetStateAsync(plan, target, TargetExecutionState.Executing, observer, cancellationToken).ConfigureAwait(false);
            var progressReporter = CreateProgressReporter(plan, target, observer, cancellationToken);
            var credential = ResolveTargetCredential(plan, target);
            var execution = await transportExecutor.ExecuteScriptAsync(
                new TransportScriptExecutionRequest(plan, target, targetPreparation, progressReporter, credential),
                cancellationToken).ConfigureAwait(false);

            await NotifyTargetStateAsync(plan, target, TargetExecutionState.CollectingArtifacts, observer, cancellationToken).ConfigureAwait(false);
            var artifacts = await artifactCollector.CollectAsync(plan, target, cancellationToken, progressReporter).ConfigureAwait(false);

            var result = await CreateExecutionResultAsync(plan, target, execution, artifacts, cancellationToken).ConfigureAwait(false);
            await NotifyTargetStateAsync(observer, result, result.EndedAt, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var endedAt = clock.UtcNow;
            var failure = CreateFailureResult(
                plan,
                target,
                endedAt,
                endedAt,
                FailureCategory.InternalError,
                $"Target execution failed unexpectedly for '{target.Target.Name}': {exception.Message}");
            await NotifyTargetStateAsync(observer, failure, endedAt, cancellationToken).ConfigureAwait(false);
            return failure;
        }
    }

    private async Task<TargetScriptPreparationResult> PrepareTargetAsync(
        ExecutionPlan plan,
        TargetExecution target,
        IDispatchExecutionObserver observer,
        CancellationToken cancellationToken)
    {
        if (!RequiresPreparation(plan))
        {
            return new TargetScriptPreparationResult(
                Target: target.Target,
                RemoteScriptPath: target.PlannedRemoteScriptPath ?? string.Empty,
                AdminShareScriptPath: null,
                Succeeded: true);
        }

        await NotifyTargetStateAsync(plan, target, TargetExecutionState.PreparingScript, observer, cancellationToken).ConfigureAwait(false);
        var targetPlan = plan with { Targets = [target] };
        var preparation = await scriptPreparationService.PrepareAsync(targetPlan, cancellationToken).ConfigureAwait(false);
        var targetPreparation = preparation.Targets.SingleOrDefault(result =>
            result.Target.Name.Equals(target.Target.Name, StringComparison.OrdinalIgnoreCase));

        return targetPreparation ?? new TargetScriptPreparationResult(
            Target: target.Target,
            RemoteScriptPath: target.PlannedRemoteScriptPath ?? string.Empty,
            AdminShareScriptPath: null,
            Succeeded: false,
            FailureCategory: FailureCategory.PayloadPreparationFailed,
            FailureMessage: $"No script preparation result exists for target '{target.Target.Name}'.");
    }

    private static bool RequiresPreparation(ExecutionPlan plan) =>
        plan.Job.Payload is ScriptPayload
        && plan.Job.ScriptTransferPolicy.RequiresEndpointLocalScriptPath;

    private static async Task NotifyTargetStateAsync(
        ExecutionPlan plan,
        TargetExecution target,
        TargetExecutionState state,
        IDispatchExecutionObserver observer,
        CancellationToken cancellationToken)
    {
        await NotifyTargetStateAsync(
            observer,
            new DispatchExecutionProgress(
                plan.RunId,
                target.Target.Name,
                state,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    private static Task NotifyTargetStateAsync(
        IDispatchExecutionObserver observer,
        TargetExecutionResult result,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken) =>
        NotifyTargetStateAsync(
            observer,
            new DispatchExecutionProgress(
                result.RunId,
                result.Target,
                result.State,
                timestamp,
                result.FailureCategory,
                result.FailureMessage),
            cancellationToken);

    private static async Task NotifyTargetStateAsync(
        IDispatchExecutionObserver observer,
        DispatchExecutionProgress progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await observer.OnProgressAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }
    }

    private static Action<DispatchExecutionProgress> CreateProgressReporter(
        ExecutionPlan plan,
        TargetExecution target,
        IDispatchExecutionObserver observer,
        CancellationToken cancellationToken) =>
        progress =>
        {
            var normalized = progress with
            {
                RunId = string.IsNullOrWhiteSpace(progress.RunId) ? plan.RunId : progress.RunId,
                Target = string.IsNullOrWhiteSpace(progress.Target) ? target.Target.Name : progress.Target
            };
            NotifyTargetStateAsync(observer, normalized, cancellationToken).GetAwaiter().GetResult();
        };

    private static async Task<TargetExecutionResult> CreateExecutionResultAsync(
        ExecutionPlan plan,
        TargetExecution target,
        TransportScriptExecutionResult execution,
        ArtifactCollectionResult artifacts,
        CancellationToken cancellationToken)
    {
        var state = execution.FailureCategory switch
        {
            FailureCategory.None => TargetExecutionState.Succeeded,
            FailureCategory.TimedOut => TargetExecutionState.TimedOut,
            _ => TargetExecutionState.Failed
        };
        var stdoutPath = Path.Combine(target.PlannedLocalTargetRoot ?? string.Empty, "stdout.txt");
        var stderrPath = Path.Combine(target.PlannedLocalTargetRoot ?? string.Empty, "stderr.txt");

        if (!string.IsNullOrWhiteSpace(target.PlannedLocalTargetRoot))
        {
            Directory.CreateDirectory(target.PlannedLocalTargetRoot);
            await File.WriteAllTextAsync(stdoutPath, execution.Stdout, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(stderrPath, execution.Stderr, cancellationToken).ConfigureAwait(false);
        }

        return new TargetExecutionResult(
            RunId: plan.RunId,
            Target: target.Target.Name,
            Transport: plan.Job.Transport,
            PayloadType: plan.Job.Payload.PayloadType,
            PayloadName: plan.Job.Payload.DisplayName,
            State: state,
            ExitCode: execution.ExitCode,
            ExpectedExitCodes: plan.Job.ExpectedExitCodes,
            StartedAt: execution.StartedAt,
            EndedAt: execution.EndedAt,
            FailureCategory: execution.FailureCategory,
            FailureMessage: execution.FailureMessage,
            StdoutPath: stdoutPath,
            StderrPath: stderrPath,
            ResultPath: plan.Job.ResultPolicy.WritePerTargetJson ? target.PlannedLocalResultPath ?? string.Empty : string.Empty,
            Artifacts: artifacts.Artifacts,
            ArtifactCollectionStatus: artifacts.Status,
            ArtifactCollectionFailureMessage: artifacts.FailureMessage,
            SecretHandoffStatus: GetCredentialHandoffStatus(plan, target),
            CleanupStatus: "not-started",
            TransportMetadata: execution.Metadata,
            StreamRecords: execution.StreamRecords);
    }

    private static TargetExecutionResult CreateFailureResult(
        ExecutionPlan plan,
        TargetExecution target,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        FailureCategory failureCategory,
        string failureMessage,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(
            RunId: plan.RunId,
            Target: target.Target.Name,
            Transport: plan.Job.Transport,
            PayloadType: plan.Job.Payload.PayloadType,
            PayloadName: plan.Job.Payload.DisplayName,
            State: failureCategory == FailureCategory.TimedOut ? TargetExecutionState.TimedOut : TargetExecutionState.Failed,
            ExitCode: null,
            ExpectedExitCodes: plan.Job.ExpectedExitCodes,
            StartedAt: startedAt,
            EndedAt: endedAt,
            FailureCategory: failureCategory,
            FailureMessage: failureMessage,
            ResultPath: plan.Job.ResultPolicy.WritePerTargetJson ? target.PlannedLocalResultPath ?? string.Empty : string.Empty,
            Artifacts: [],
            ArtifactCollectionStatus: "skipped",
            SecretHandoffStatus: GetCredentialHandoffStatus(plan, target),
            CleanupStatus: "not-started",
            TransportMetadata: metadata);

    private static Dispatch.Core.Credentials.DispatchResolvedCredential? ResolveTargetCredential(
        ExecutionPlan plan,
        TargetExecution target)
    {
        var reference = target.Target.CredentialReference;
        return string.IsNullOrWhiteSpace(reference)
            ? null
            : plan.RuntimeCredentials.TryGetValue(reference.Trim(), out var credential)
                ? credential
                : null;
    }

    private static string GetCredentialHandoffStatus(ExecutionPlan plan, TargetExecution target)
    {
        var reference = target.Target.CredentialReference;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return "not-supported";
        }

        return plan.RuntimeCredentials.ContainsKey(reference.Trim()) ? "resolved" : "not-resolved";
    }

    private static DispatchEventStreamWriter? CreateEventStreamWriter(ExecutionPlan plan)
    {
        if (!plan.Job.ResultPolicy.WriteEventStream || string.IsNullOrWhiteSpace(plan.LocalEventsNdjsonPath))
        {
            return null;
        }

        return new DispatchEventStreamWriter(plan.LocalEventsNdjsonPath);
    }

    private static void WriteFinalEvents(DispatchEventStreamWriter? eventStreamWriter, DispatchRunResult result)
    {
        if (eventStreamWriter is null)
        {
            return;
        }

        foreach (var target in result.Targets)
        {
            eventStreamWriter.WriteTargetResult(target);
        }

        eventStreamWriter.WriteRunResult(result);
    }
}
