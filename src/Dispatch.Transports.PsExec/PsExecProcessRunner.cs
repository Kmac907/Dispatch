using System.Diagnostics;
using Dispatch.Core.Models;

namespace Dispatch.Transports.PsExec;

public sealed class PsExecProcessRunner : IPsExecProcessRunner
{
    public async Task<PsExecProcessResult> RunAsync(PsExecCommand command, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.Executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (var argument in command.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            var endedAt = DateTimeOffset.UtcNow;
            return new PsExecProcessResult(
                ExitCode: null,
                Stdout: string.Empty,
                Stderr: exception.Message,
                StartedAt: startedAt,
                EndedAt: endedAt,
                FailureCategory: FailureCategory.TransportUnavailable,
                FailureMessage: $"Could not start PsExec process '{command.Executable}': {exception.Message}");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new PsExecProcessResult(
            ExitCode: process.ExitCode,
            Stdout: await stdout.ConfigureAwait(false),
            Stderr: await stderr.ConfigureAwait(false),
            StartedAt: startedAt,
            EndedAt: DateTimeOffset.UtcNow);
    }
}
