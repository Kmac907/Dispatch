using Dispatch.Core.Validation;

namespace Dispatch.Core.Execution;

public sealed record AdminSharePathResult(
    string? Path,
    DispatchValidationError? Error)
{
    public bool IsValid => Error is null && Path is not null;
}

public static class AdminSharePath
{
    public static AdminSharePathResult FromRemoteWindowsPath(string targetName, string remotePath)
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return Error("TargetNameRequired", "Target name is required for admin-share path conversion.");
        }

        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return Error("RemotePathRequired", "Remote path is required for admin-share path conversion.");
        }

        var normalizedPath = remotePath.Replace('/', '\\');
        if (normalizedPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return Error("RemotePathMustBeDrivePath", $"Remote path '{remotePath}' must be a drive-qualified Windows path.");
        }

        if (normalizedPath.Length < 3
            || !char.IsLetter(normalizedPath[0])
            || normalizedPath[1] != ':'
            || normalizedPath[2] != '\\')
        {
            return Error("RemotePathMustBeDrivePath", $"Remote path '{remotePath}' must be a drive-qualified Windows path.");
        }

        var drive = char.ToUpperInvariant(normalizedPath[0]);
        var relativePath = normalizedPath[3..].TrimStart('\\');
        var sharePath = relativePath.Length == 0
            ? $@"\\{targetName}\{drive}$"
            : $@"\\{targetName}\{drive}$\{relativePath}";

        return new AdminSharePathResult(sharePath, null);
    }

    private static AdminSharePathResult Error(string code, string message) =>
        new(null, new DispatchValidationError(code, message));
}
