using Dispatch.Core.Models;
using Dispatch.Core.Transports;
using System.Runtime.Versioning;

namespace Dispatch.Transports.Psrp;

[SupportedOSPlatform("windows")]
public sealed class PsrpScriptExecutor(
    IPsrpCommandClient commandClient,
    IPsrpScriptClient scriptClient) : ITransportScriptExecutor
{
    public TransportKind Kind => TransportKind.Psrp;

    public async Task<TransportScriptExecutionResult> ExecuteScriptAsync(
        TransportScriptExecutionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startedAt = DateTimeOffset.UtcNow;
        var executionContext = request.Plan.Job.ExecutionContext;
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["transport"] = "psrp",
            ["mode"] = "execution-pending",
            ["payloadType"] = request.Plan.Job.Payload.PayloadType.ToString().ToLowerInvariant(),
            ["executionStatus"] = "pending",
            ["configurationName"] = PsrpCommandClient.NormalizeConfigurationName(executionContext.PsrpConfigurationName),
            ["connectionKind"] = PsrpCommandClient.NormalizeConnectionKind(executionContext.PsrpConnectionKind).ToString().ToLowerInvariant(),
            ["authentication"] = PsrpCommandClient.NormalizeAuthenticationKind(executionContext.PsrpAuthentication).ToString().ToLowerInvariant()
        };

        if (request.Plan.Job.Payload is ScriptPayload scriptPayload)
        {
            var remoteScriptPath = request.Target.PlannedRemoteScriptPath ?? request.Preparation.RemoteScriptPath;
            if (string.IsNullOrWhiteSpace(remoteScriptPath))
            {
                metadata["mode"] = "execution-unavailable";
                metadata["executionStatus"] = "failed";
                return new TransportScriptExecutionResult(
                    ExitCode: null,
                    Stdout: string.Empty,
                    Stderr: string.Empty,
                    StartedAt: startedAt,
                    EndedAt: DateTimeOffset.UtcNow,
                    FailureCategory: FailureCategory.PayloadPreparationFailed,
                    FailureMessage: $"PSRP script execution requires a planned remote script path for '{request.Target.Target.Name}'.",
                    Metadata: metadata,
                    StreamRecords: null);
            }

            metadata["remoteScriptPath"] = remoteScriptPath;
            metadata["executionCommand"] = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File {remoteScriptPath}";

            var scriptResult = await scriptClient.ExecuteAsync(
                new PsrpScriptRequest(
                    request.Target.Target.Name,
                    scriptPayload.ScriptPath,
                    scriptPayload.ScriptArguments,
                    request.Plan.Job.TimeoutPolicy.ExecutionTimeout,
                    remoteScriptPath,
                    executionContext.PsrpConfigurationName,
                    executionContext.PsrpConnectionKind,
                    executionContext.PsrpAuthentication,
                    executionContext.PsrpCertificateThumbprint),
                cancellationToken).ConfigureAwait(false);

            return CreateResult(request, startedAt, metadata, scriptResult);
        }

        var commandPayload = (CommandPayload)request.Plan.Job.Payload;
        if (request.Target.PlannedCommand is null)
        {
            metadata["mode"] = "execution-unavailable";
            metadata["executionStatus"] = "failed";
            return new TransportScriptExecutionResult(
                ExitCode: null,
                Stdout: string.Empty,
                Stderr: string.Empty,
                StartedAt: startedAt,
                EndedAt: DateTimeOffset.UtcNow,
                FailureCategory: FailureCategory.PayloadPreparationFailed,
                FailureMessage: $"PSRP command execution requires a prepared command for '{request.Target.Target.Name}'.",
                Metadata: metadata,
                StreamRecords: null);
        }

        metadata["commandShell"] = commandPayload.Shell;
        if (!string.IsNullOrWhiteSpace(commandPayload.WorkingDirectory))
        {
            metadata["workingDirectory"] = commandPayload.WorkingDirectory;
        }

        metadata["executionCommand"] = request.Target.PlannedCommand.RenderedCommand;

        var commandResult = await commandClient.ExecuteAsync(
            new PsrpCommandRequest(
                request.Target.Target.Name,
                request.Target.PlannedCommand.Executable,
                RenderArguments(request.Target.PlannedCommand.Arguments),
                commandPayload.WorkingDirectory,
                request.Plan.Job.TimeoutPolicy.ExecutionTimeout,
                executionContext.PsrpConfigurationName,
                executionContext.PsrpConnectionKind,
                executionContext.PsrpAuthentication,
                executionContext.PsrpCertificateThumbprint),
            cancellationToken).ConfigureAwait(false);

        return CreateResult(request, startedAt, metadata, commandResult);
    }

    private static TransportScriptExecutionResult CreateResult(
        TransportScriptExecutionRequest request,
        DateTimeOffset startedAt,
        Dictionary<string, string> metadata,
        PsrpCommandResult result)
    {
        if (result.Metadata is not null)
        {
            foreach (var pair in result.Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }
        }

        metadata["executable"] = request.Target.PlannedCommand?.Executable ?? "powershell.exe";

        if (!result.Succeeded)
        {
            metadata["mode"] = "execution-failed";
            metadata["executionStatus"] = "failed";
            metadata["failureCategory"] = result.FailureCategory.ToString();
            return new TransportScriptExecutionResult(
                ExitCode: result.ExitCode,
                Stdout: result.Stdout,
                Stderr: result.Stderr,
                StartedAt: startedAt,
                EndedAt: DateTimeOffset.UtcNow,
                FailureCategory: result.FailureCategory,
                FailureMessage: result.FailureMessage,
                Metadata: metadata,
                StreamRecords: result.StreamRecords);
        }

        var effectiveExitCode = result.ExitCode ?? 0;
        var succeeded = request.Plan.Job.ExpectedExitCodes.Contains(effectiveExitCode);
        metadata["mode"] = "executed";
        metadata["executionStatus"] = "completed";

        return new TransportScriptExecutionResult(
            ExitCode: effectiveExitCode,
            Stdout: result.Stdout,
            Stderr: result.Stderr,
            StartedAt: startedAt,
            EndedAt: DateTimeOffset.UtcNow,
            FailureCategory: succeeded ? FailureCategory.None : FailureCategory.UnexpectedExitCode,
            FailureMessage: succeeded
                ? null
                : $"PSRP execution exited with code {effectiveExitCode}; expected {string.Join(", ", request.Plan.Job.ExpectedExitCodes)}.",
            Metadata: metadata,
            StreamRecords: result.StreamRecords);
    }

    private static string RenderArguments(IReadOnlyList<string> arguments) =>
        string.Join(" ", arguments.Select(QuoteIfNeeded));

    private static string QuoteIfNeeded(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }
}
