namespace Dispatch.Transports.PsExec;

public sealed class PsExecAdminShareProbe : IPsExecAdminShareProbe
{
    public Task<PsExecAdminShareProbeResult> ProbeDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var exists = Directory.Exists(path);
            return Task.FromResult(exists
                ? PsExecAdminShareProbeResult.Success
                : PsExecAdminShareProbeResult.Failed("Admin share path is not reachable or does not exist."));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Task.FromResult(PsExecAdminShareProbeResult.Failed(exception.Message, PsExecAdminShareFailureKind.Authorization));
        }
        catch (IOException exception)
        {
            return Task.FromResult(PsExecAdminShareProbeResult.Failed(exception.Message));
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(PsExecAdminShareProbeResult.Failed(exception.Message));
        }
        catch (NotSupportedException exception)
        {
            return Task.FromResult(PsExecAdminShareProbeResult.Failed(exception.Message));
        }
    }
}
