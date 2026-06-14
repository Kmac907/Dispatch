using Dispatch.Core.Models;
using Dispatch.Core.Transports;

namespace Dispatch.Core.Execution;

internal sealed class DispatchExecutor(
    IScriptPreparationService scriptPreparationService,
    IEnumerable<ITransportScriptExecutor> transportExecutors,
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

            return new DispatchRunResult(
                RunId: plan.RunId,
                StartedAt: startedAt,
                EndedAt: unavailableEndedAt,
                RequestedBy: Environment.UserName,
                Transport: plan.Job.Transport,
                PayloadType: plan.Job.Payload.PayloadType,
                PayloadName: plan.Job.Payload.DisplayName,
                Targets: unavailableTargets,
                ResultPath: plan.LocalResultsJsonPath);
        }

        var preparation = await scriptPreparationService.PrepareAsync(plan, cancellationToken).ConfigureAwait(false);
        var targetResults = new List<TargetExecutionResult>();

        foreach (var target in plan.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetPreparation = preparation.Targets.SingleOrDefault(result => result.Target.Name.Equals(target.Target.Name, StringComparison.OrdinalIgnoreCase));
            if (targetPreparation is null)
            {
                targetResults.Add(CreateFailureResult(
                    plan,
                    target,
                    startedAt,
                    clock.UtcNow,
                    FailureCategory.PayloadPreparationFailed,
                    $"No script preparation result exists for target '{target.Target.Name}'."));
                continue;
            }

            if (!targetPreparation.Succeeded)
            {
                targetResults.Add(CreateFailureResult(
                    plan,
                    target,
                    startedAt,
                    clock.UtcNow,
                    targetPreparation.FailureCategory,
                    targetPreparation.FailureMessage ?? $"Script preparation failed for target '{target.Target.Name}'."));
                continue;
            }

            var execution = await transportExecutor.ExecuteScriptAsync(
                new TransportScriptExecutionRequest(plan, target, targetPreparation),
                cancellationToken).ConfigureAwait(false);

            var state = execution.FailureCategory == FailureCategory.None
                ? TargetExecutionState.Succeeded
                : TargetExecutionState.Failed;

            targetResults.Add(new TargetExecutionResult(
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
                ResultPath: target.PlannedLocalResultPath ?? string.Empty,
                SecretHandoffStatus: "not-supported",
                CleanupStatus: "not-started",
                TransportMetadata: execution.Metadata));
        }

        var endedAt = clock.UtcNow;
        return new DispatchRunResult(
            RunId: plan.RunId,
            StartedAt: startedAt,
            EndedAt: endedAt,
            RequestedBy: Environment.UserName,
            Transport: plan.Job.Transport,
            PayloadType: plan.Job.Payload.PayloadType,
            PayloadName: plan.Job.Payload.DisplayName,
            Targets: targetResults,
            ResultPath: plan.LocalResultsJsonPath);
    }

    private static TargetExecutionResult CreateFailureResult(
        ExecutionPlan plan,
        TargetExecution target,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        FailureCategory failureCategory,
        string failureMessage) =>
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
            SecretHandoffStatus: "not-supported",
            CleanupStatus: "not-started");
}
