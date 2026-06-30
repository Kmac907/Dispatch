using Microsoft.Extensions.Configuration;

namespace Dispatch.Cli;

internal static class DispatchConfigFileReader
{
    private static readonly HashSet<string> ForbiddenSecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "secret",
        "token",
        "sas"
    };

    public static IConfigurationRoot Load(string path)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return new ConfigurationBuilder()
                .AddJsonFile(path, optional: false)
                .Build();
        }

        var values = ReadYamlFile(path);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    public static IReadOnlyDictionary<string, string?> ReadYamlFile(string path) =>
        ReadYaml(File.ReadAllLines(path), path);

    public static IReadOnlyDictionary<string, string?> ReadYaml(IEnumerable<string> lines, string sourceName)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<YamlPathPart>();
        var lineNumber = 0;

        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = rawLine.Split('#', 2)[0].TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new FormatException($"{sourceName}:{lineNumber}: expected a YAML mapping entry.");
            }

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new FormatException($"{sourceName}:{lineNumber}: empty YAML keys are not supported.");
            }

            while (stack.Count > 0 && stack[^1].Indent >= indent)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            var pathParts = stack.Select(static item => item.Key).Append(key).ToArray();
            RejectPlaintextSecretKeys(pathParts, sourceName, lineNumber);

            if (value.Length == 0)
            {
                stack.Add(new YamlPathPart(indent, key));
                continue;
            }

            var configurationKey = ToConfigurationKey(pathParts);
            if (!string.IsNullOrWhiteSpace(configurationKey))
            {
                values[configurationKey] = Unquote(value);
            }
        }

        return values;
    }

    private static void RejectPlaintextSecretKeys(IReadOnlyList<string> pathParts, string sourceName, int lineNumber)
    {
        var key = pathParts[^1];
        if (ForbiddenSecretKeys.Contains(key))
        {
            throw new InvalidDataException($"{sourceName}:{lineNumber}: plaintext secret field '{key}' is not allowed.");
        }

        if (pathParts.Count >= 2
            && pathParts[^2].Equals("credential", StringComparison.OrdinalIgnoreCase)
            && key.Equals("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{sourceName}:{lineNumber}: plaintext credential password fields are not allowed.");
        }
    }

    private static string ToConfigurationKey(IReadOnlyList<string> pathParts)
    {
        if (pathParts.Count == 0)
        {
            return string.Empty;
        }

        if (pathParts[0].Equals("dispatch", StringComparison.OrdinalIgnoreCase))
        {
            var mapped = pathParts
                .Skip(1)
                .Select(MapDispatchKey)
                .ToArray();
            return mapped.Length == 0 ? string.Empty : $"Dispatch:{string.Join(':', mapped)}";
        }

        if (pathParts[0].Equals("credentials", StringComparison.OrdinalIgnoreCase))
        {
            var mapped = pathParts
                .Skip(1)
                .Select(static (part, index) => index == 0 ? part : ToPascalCase(part))
                .ToArray();
            return mapped.Length == 0 ? string.Empty : $"Credentials:{string.Join(':', mapped)}";
        }

        return string.Join(':', pathParts.Select(static part => ToPascalCase(part)));
    }

    private static string MapDispatchKey(string key) =>
        key.ToLowerInvariant() switch
        {
            "default_transport" => "DefaultTransport",
            "defaultcredentialprovider" or "default_credential_provider" => "CredentialProvider",
            "credential_provider" => "CredentialProvider",
            "credential_store_path" => "CredentialStorePath",
            "local_run_root" => "LocalRunRoot",
            "remote_run_root" => "RemoteRunRoot",
            "expected_exit_codes" => "ExpectedExitCodes",
            "psexec_path" => "PsExecPath",
            "allow_run_as_system" => "AllowRunAsSystem",
            "allow_psexec_fallback" => "AllowPsExecFallback",
            _ => ToPascalCase(key)
        };

    private static string ToPascalCase(string key)
    {
        var parts = key.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0
            ? key
            : string.Concat(parts.Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private sealed record YamlPathPart(int Indent, string Key);
}
