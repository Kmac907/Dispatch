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
    public async Task<DispatchRunResult> ExecuteAsync(ExecutionPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (plan.DryRun)
        {
            throw new InvalidOperationException("Dry-run plans cannot be executed.");
        }

        var startedAt = clock.UtcNow;
        var transportExecutor = transportExecutors.SingleOrDefault(executor => executor.Kind == plan.Job.Transport);

        if (transportExecutor is null)
        {
            var unavailableEndedAt = clock.UtcNow;
            var unavailableTargets = plan.Targets
                .Select(target => CreateFailureResult(
                    plan,
                    target,
                    startedAt,
                    unavailableEndedAt,
                    FailureCategory.TransportUnavailable,
                    $"No executor is registered for transport '{plan.Job.Transport.ToDispatchString()}'."))
                .ToArray();

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

            await resultWriter.WriteAsync(plan, unavailableResult, cancellationToken).ConfigureAwait(false);
            return unavailableResult;
        }

        var endpointProbe = endpointProbes.SingleOrDefault(probe => probe.Kind == plan.Job.Transport);
        var targetResults = await ExecuteTargetsAsync(plan, transportExecutor, endpointProbe, cancellationToken).ConfigureAwait(false);

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

        await resultWriter.WriteAsync(plan, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<IReadOnlyList<TargetExecutionResult>> ExecuteTargetsAsync(
        ExecutionPlan plan,
        ITransportScriptExecutor transportExecutor,
        ITransportEndpointProbe? endpointProbe,
        CancellationToken cancellationToken)
    {
        var throttleLimit = Math.Max(1, plan.ThrottleLimit);
        using var semaphore = new SemaphoreSlim(throttleLimit, throttleLimit);
        var tasks = plan.Targets.Select(async (target, index) =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await ExecuteTargetAsync(plan, target, transportExecutor, endpointProbe, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        try
        {
            if (endpointProbe is not null)
            {
                var probeResult = await endpointProbe.ProbeAsync(new TransportEndpointProbeRequest(plan, target), cancellationToken).ConfigureAwait(false);
                if (!probeResult.Succeeded)
                {
                    return CreateFailureResult(
                        plan,
                        target,
                        probeResult.StartedAt,
                        probeResult.EndedAt,
                        probeResult.FailureCategory,
                        probeResult.FailureMessage ?? $"Endpoint probe failed for target '{target.Target.Name}'.",
                        probeResult.Metadata);
                }
            }

            var targetPlan = plan with { Targets = [target] };
            var preparation = await scriptPreparationService.PrepareAsync(targetPlan, cancellationToken).ConfigureAwait(false);
            var targetPreparation = preparation.Targets.SingleOrDefault(result =>
                result.Target.Name.Equals(target.Target.Name, StringComparison.OrdinalIgnoreCase));

            if (targetPreparation is null)
            {
                return CreateFailureResult(
                    plan,
                    target,
                    clock.UtcNow,
                    clock.UtcNow,
                    FailureCategory.PayloadPreparationFailed,
                    $"No script preparation result exists for target '{target.Target.Name}'.");
            }

            if (!targetPreparation.Succeeded)
            {
                return CreateFailureResult(
                    plan,
                    target,
                    clock.UtcNow,
                    clock.UtcNow,
                    targetPreparation.FailureCategory,
                    targetPreparation.FailureMessage ?? $"Script preparation failed for target '{target.Target.Name}'.");
            }

            var execution = await transportExecutor.ExecuteScriptAsync(
                new TransportScriptExecutionRequest(plan, target, targetPreparation),
                cancellationToken).ConfigureAwait(false);
            var artifacts = await artifactCollector.CollectAsync(plan, target, cancellationToken).ConfigureAwait(false);

            return await CreateExecutionResultAsync(plan, target, execution, artifacts, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var endedAt = clock.UtcNow;
            return CreateFailureResult(
                plan,
                target,
                endedAt,
                endedAt,
                FailureCategory.InternalError,
                $"Target execution failed unexpectedly for '{target.Target.Name}': {exception.Message}");
        }
    }

    private static async Task<TargetExecutionResult> CreateExecutionResultAsync(
        ExecutionPlan plan,
        TargetExecution target,
        TransportScriptExecutionResult execution,
        ArtifactCollectionResult artifacts,
        CancellationToken cancellationToken)
    {
        var state = execution.FailureCategory == FailureCategory.None
            ? TargetExecutionState.Succeeded
            : TargetExecutionState.Failed;
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
            ResultPath: target.PlannedLocalResultPath ?? string.Empty,
            Artifacts: artifacts.Artifacts,
            ArtifactCollectionStatus: artifacts.Status,
            ArtifactCollectionFailureMessage: artifacts.FailureMessage,
            SecretHandoffStatus: "not-supported",
            CleanupStatus: "not-started",
            TransportMetadata: execution.Metadata);
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
            State: TargetExecutionState.Failed,
            ExitCode: null,
            ExpectedExitCodes: plan.Job.ExpectedExitCodes,
            StartedAt: startedAt,
            EndedAt: endedAt,
            FailureCategory: failureCategory,
            FailureMessage: failureMessage,
            ResultPath: target.PlannedLocalResultPath ?? string.Empty,
            Artifacts: [],
            ArtifactCollectionStatus: "skipped",
            SecretHandoffStatus: "not-supported",
            CleanupStatus: "not-started",
            TransportMetadata: metadata);
}
