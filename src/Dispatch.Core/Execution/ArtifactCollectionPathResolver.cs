using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

public sealed record ArtifactCollectionPath(
    string ConfiguredPath,
    string RemoteFolder,
    string LocalRelativeRoot);

public static class ArtifactCollectionPathResolver
{
    private static readonly string[] DefaultArtifactFolders = ["logs", "artifacts"];

    public static IReadOnlyList<ArtifactCollectionPath> ResolveAll(ArtifactPolicy policy, string remoteRunRoot) =>
        GetArtifactFolders(policy)
            .Select(path => Resolve(path, remoteRunRoot))
            .ToArray();

    public static ArtifactCollectionPath Resolve(string configuredPath, string remoteRunRoot)
    {
        var normalized = NormalizeWindowsPath(configuredPath);
        if (IsDriveQualifiedAbsoluteWindowsPath(normalized))
        {
            return new ArtifactCollectionPath(
                configuredPath,
                normalized.TrimEnd('\\'),
                ToLocalRelativeRoot(normalized));
        }

        return new ArtifactCollectionPath(
            configuredPath,
            CombineWindowsPath(remoteRunRoot, normalized),
            SanitizeRelativePath(normalized));
    }

    public static bool IsDriveQualifiedAbsoluteWindowsPath(string value)
    {
        var normalized = NormalizeWindowsPath(value);
        return normalized.Length >= 3
            && char.IsLetter(normalized[0])
            && normalized[1] == ':'
            && normalized[2] == '\\';
    }

    public static string NormalizeWindowsPath(string value) =>
        value.Trim().Replace('/', '\\');

    public static string CombineWindowsPath(params string[] parts)
    {
        var first = parts[0].TrimEnd('\\');
        var rest = parts
            .Skip(1)
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .Select(static part => part.Trim('\\'));

        return string.Join('\\', new[] { first }.Concat(rest));
    }

    public static string SanitizeRelativePath(string value)
    {
        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var segments = NormalizeWindowsPath(value)
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => string.Concat(segment.Select(character => invalidFileNameChars.Contains(character) ? '_' : character)))
            .Where(static segment => !string.IsNullOrWhiteSpace(segment));

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static IReadOnlyList<string> GetArtifactFolders(ArtifactPolicy policy) =>
        policy.Paths is { Count: > 0 }
            ? policy.Paths
            : DefaultArtifactFolders;

    private static string ToLocalRelativeRoot(string absoluteRemotePath)
    {
        var normalized = NormalizeWindowsPath(absoluteRemotePath);
        var drive = char.ToUpperInvariant(normalized[0]).ToString();
        var relative = normalized.Length > 3
            ? normalized[3..]
            : string.Empty;

        return Path.Combine("external", drive, SanitizeRelativePath(relative));
    }
}
