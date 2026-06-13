namespace Dispatch.Core.Models;

public sealed record TargetSpec(string Name, string? Source = null);

public sealed record ExecutionContextOptions(bool RunAsSystem = false, string? WorkingDirectory = null);

public sealed record ScriptTransferPolicy(string RemoteRoot, bool RequiresEndpointLocalScriptPath);

public sealed record TimeoutPolicy(TimeSpan? ExecutionTimeout = null, TimeSpan? ConnectionTimeout = null);

public sealed record RetryPolicy(int MaxAttempts = 1);

public sealed record ArtifactPolicy(IReadOnlyList<string>? Paths = null);

public sealed record ResultPolicy(string LocalRunRoot, bool WriteJson = true, bool WriteCsv = true);
