using Dispatch.Core.Defaults;
using Dispatch.Core.Models;

namespace Dispatch.Core.Configuration;

public sealed class DispatchOptions
{
    public const string SectionName = "Dispatch";

    public string? Inventory { get; set; }

    public string? Target { get; set; }

    public string? Exclude { get; set; }

    public string LocalRunRoot { get; set; } = DispatchDefaults.LocalRunRoot;

    public string RemoteRunRoot { get; set; } = DispatchDefaults.RemoteRunRoot;

    public TransportKind DefaultTransport { get; set; } = TransportKind.PsExec;

    public int Throttle { get; set; } = DispatchDefaults.DefaultThrottle;

    public int[] ExpectedExitCodes { get; set; } = [];

    public string PsExecPath { get; set; } = "psexec.exe";

    public string CredentialProvider { get; set; } = "none";

    public string CredentialStorePath { get; set; } = DispatchDefaults.CredentialStorePath;

    public bool AllowRunAsSystem { get; set; }

    public bool AllowPsExecFallback { get; set; }
}
