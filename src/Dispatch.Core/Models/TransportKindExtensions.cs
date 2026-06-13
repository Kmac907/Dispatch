namespace Dispatch.Core.Models;

public static class TransportKindExtensions
{
    public static string ToDispatchString(this TransportKind transport) =>
        transport switch
        {
            TransportKind.PsExec => "psexec",
            TransportKind.Psrp => "psrp",
            TransportKind.WinRm => "winrm",
            _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unknown transport kind.")
        };
}
