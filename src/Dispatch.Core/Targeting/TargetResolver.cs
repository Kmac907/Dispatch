using Dispatch.Core.Models;
using Dispatch.Core.Validation;

namespace Dispatch.Core.Targeting;

public static class TargetResolver
{
    public static TargetResolutionResult Resolve(TargetResolutionInput input)
    {
        var targets = new List<TargetSpec>();
        var errors = new List<DispatchValidationError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inventory = InventoryTargets.Empty;

        foreach (var value in input.ComputerNameValues ?? Array.Empty<string>())
        {
            AddComputerNameTargets(value, targets, seen);
        }

        if (!string.IsNullOrWhiteSpace(input.TargetFile))
        {
            AddTargetFileTargets(input.TargetFile, targets, seen, errors);
        }

        if (!string.IsNullOrWhiteSpace(input.InventoryPath))
        {
            inventory = ReadInventory(input.InventoryPath, errors);
        }

        foreach (var selector in input.TargetSelectors ?? Array.Empty<string>())
        {
            AddSelectorTargets(selector, inventory, targets, seen, errors);
        }

        if (targets.Count == 0 && errors.Count == 0 && !string.IsNullOrWhiteSpace(input.InventoryPath))
        {
            foreach (var target in inventory.AllHosts)
            {
                AddTarget(target.Name, target.Source, targets, seen);
            }
        }

        foreach (var selector in input.ExcludeSelectors ?? Array.Empty<string>())
        {
            ExcludeSelectorTargets(selector, inventory, targets);
        }

        if (targets.Count == 0 && errors.Count == 0)
        {
            errors.Add(new("TargetsRequired", "At least one target is required from --target, --inventory, --computer-name, or --target-file."));
        }

        return new TargetResolutionResult(targets, errors);
    }

    private static void AddComputerNameTargets(
        string value,
        ICollection<TargetSpec> targets,
        ISet<string> seen)
    {
        foreach (var target in SplitTargets(value))
        {
            AddTarget(target, "computer-name", targets, seen);
        }
    }

    private static void AddTargetFileTargets(
        string targetFile,
        ICollection<TargetSpec> targets,
        ISet<string> seen,
        ICollection<DispatchValidationError> errors)
    {
        if (!File.Exists(targetFile))
        {
            errors.Add(new("TargetFileNotFound", $"Target file '{targetFile}' does not exist."));
            return;
        }

        var lineNumber = 0;
        foreach (var line in File.ReadLines(targetFile))
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            foreach (var target in SplitTargets(trimmed))
            {
                AddTarget(target, $"target-file:{targetFile}:{lineNumber}", targets, seen);
            }
        }
    }

    private static void AddSelectorTargets(
        string selector,
        InventoryTargets inventory,
        ICollection<TargetSpec> targets,
        ISet<string> seen,
        ICollection<DispatchValidationError> errors)
    {
        foreach (var item in SplitTargets(selector))
        {
            if (item.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var target in inventory.AllHosts)
                {
                    AddTarget(target.Name, target.Source, targets, seen);
                }

                continue;
            }

            if (item.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                AddTargetFileTargets(item["file:".Length..], targets, seen, errors);
                continue;
            }

            if (item.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var target in inventory.HostsByTag(item["tag:".Length..]))
                {
                    AddTarget(target.Name, target.Source, targets, seen);
                }

                continue;
            }

            foreach (var target in inventory.ResolveGroupOrHost(item))
            {
                AddTarget(target.Name, target.Source, targets, seen);
            }

            if (!inventory.ContainsGroupOrHost(item))
            {
                AddTarget(item, "target", targets, seen);
            }
        }
    }

    private static void ExcludeSelectorTargets(
        string selector,
        InventoryTargets inventory,
        ICollection<TargetSpec> targets)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in SplitTargets(selector))
        {
            if (item.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var target in inventory.HostsByTag(item["tag:".Length..]))
                {
                    excluded.Add(target.Name);
                }

                continue;
            }

            foreach (var target in inventory.ResolveGroupOrHost(item))
            {
                excluded.Add(target.Name);
            }

            if (!inventory.ContainsGroupOrHost(item))
            {
                excluded.Add(item);
            }
        }

        if (excluded.Count == 0)
        {
            return;
        }

        var retained = targets.Where(target => !excluded.Contains(target.Name)).ToArray();
        targets.Clear();
        foreach (var target in retained)
        {
            targets.Add(target);
        }
    }

    private static InventoryTargets ReadInventory(string inventoryPath, ICollection<DispatchValidationError> errors)
    {
        if (!File.Exists(inventoryPath))
        {
            errors.Add(new("InventoryNotFound", $"Inventory file '{inventoryPath}' does not exist."));
            return InventoryTargets.Empty;
        }

        var lines = File.ReadAllLines(inventoryPath);
        return LooksLikeYaml(lines)
            ? InventoryTargets.FromYaml(inventoryPath, lines)
            : InventoryTargets.FromText(inventoryPath, lines);
    }

    private static bool LooksLikeYaml(IEnumerable<string> lines) =>
        lines.Any(static line =>
        {
            var trimmed = line.Trim();
            return trimmed is "hosts:" or "groups:" || trimmed.StartsWith("tags:", StringComparison.OrdinalIgnoreCase);
        });

    private static IEnumerable<string> SplitTargets(string value) =>
        value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(static target => !string.IsNullOrWhiteSpace(target));

    private static void AddTarget(
        string target,
        string source,
        ICollection<TargetSpec> targets,
        ISet<string> seen)
    {
        if (!seen.Add(target))
        {
            return;
        }

        targets.Add(new TargetSpec(target, source));
    }

    private sealed class InventoryTargets
    {
        private readonly Dictionary<string, InventoryHost> hosts;
        private readonly Dictionary<string, List<string>> groups;
        private readonly Dictionary<string, List<string>> tags;

        private InventoryTargets(
            Dictionary<string, InventoryHost> hosts,
            Dictionary<string, List<string>> groups,
            Dictionary<string, List<string>> tags)
        {
            this.hosts = hosts;
            this.groups = groups;
            this.tags = tags;
        }

        public static InventoryTargets Empty { get; } = new(
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase));

        public IEnumerable<InventoryHost> AllHosts => hosts.Values;

        public static InventoryTargets FromText(string path, IReadOnlyList<string> lines)
        {
            var hosts = new Dictionary<string, InventoryHost>(StringComparer.OrdinalIgnoreCase);
            var lineNumber = 0;
            foreach (var line in lines)
            {
                lineNumber++;
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                foreach (var target in SplitTargets(trimmed))
                {
                    hosts.TryAdd(target, new(target, $"inventory:{path}:{lineNumber}"));
                }
            }

            return new(hosts, new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        }

        public static InventoryTargets FromYaml(string path, IReadOnlyList<string> lines)
        {
            var hosts = new Dictionary<string, InventoryHost>(StringComparer.OrdinalIgnoreCase);
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string? section = null;
            string? currentGroup = null;
            string? currentHost = null;
            var inGroupHosts = false;
            var inHostTags = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Split('#', 2)[0].TrimEnd();
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var indent = line.Length - line.TrimStart().Length;
                if (indent == 0 && trimmed.EndsWith(':'))
                {
                    section = trimmed.TrimEnd(':');
                    currentGroup = null;
                    currentHost = null;
                    inGroupHosts = false;
                    inHostTags = false;
                    continue;
                }

                if (section?.Equals("groups", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (indent == 2 && trimmed.EndsWith(':'))
                    {
                        currentGroup = trimmed.TrimEnd(':');
                        groups.TryAdd(currentGroup, []);
                        inGroupHosts = false;
                        continue;
                    }

                    if (indent >= 4 && trimmed.Equals("hosts:", StringComparison.OrdinalIgnoreCase))
                    {
                        inGroupHosts = currentGroup is not null;
                        continue;
                    }

                    if (inGroupHosts && trimmed.StartsWith("- ", StringComparison.Ordinal))
                    {
                        AddInventoryGroupHost(path, hosts, groups, currentGroup!, trimmed[2..].Trim());
                    }

                    continue;
                }

                if (section?.Equals("hosts", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (indent == 2 && trimmed.StartsWith("- ", StringComparison.Ordinal))
                    {
                        currentHost = trimmed[2..].Trim();
                        AddInventoryHost(path, hosts, currentHost);
                        inHostTags = false;
                        continue;
                    }

                    if (indent == 2 && trimmed.EndsWith(':'))
                    {
                        currentHost = trimmed.TrimEnd(':');
                        AddInventoryHost(path, hosts, currentHost);
                        inHostTags = false;
                        continue;
                    }

                    if (currentHost is not null && trimmed.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var tag in ParseInlineTags(trimmed["tags:".Length..]))
                        {
                            AddInventoryTag(tags, tag, currentHost);
                        }

                        inHostTags = true;
                        continue;
                    }

                    if (currentHost is not null && inHostTags && trimmed.StartsWith("- ", StringComparison.Ordinal))
                    {
                        AddInventoryTag(tags, trimmed[2..].Trim(), currentHost);
                    }
                }
            }

            return new(hosts, groups, tags);
        }

        public bool ContainsGroupOrHost(string selector) =>
            hosts.ContainsKey(selector) || groups.ContainsKey(selector);

        public IEnumerable<InventoryHost> ResolveGroupOrHost(string selector)
        {
            if (hosts.TryGetValue(selector, out var host))
            {
                yield return host;
            }

            if (!groups.TryGetValue(selector, out var groupHosts))
            {
                yield break;
            }

            foreach (var groupHost in groupHosts)
            {
                if (hosts.TryGetValue(groupHost, out var resolved))
                {
                    yield return resolved;
                }
            }
        }

        public IEnumerable<InventoryHost> HostsByTag(string tag)
        {
            if (!tags.TryGetValue(tag, out var taggedHosts))
            {
                yield break;
            }

            foreach (var taggedHost in taggedHosts)
            {
                if (hosts.TryGetValue(taggedHost, out var resolved))
                {
                    yield return resolved;
                }
            }
        }

        private static void AddInventoryGroupHost(
            string path,
            IDictionary<string, InventoryHost> hosts,
            IDictionary<string, List<string>> groups,
            string group,
            string host)
        {
            AddInventoryHost(path, hosts, host);
            groups[group].Add(host);
        }

        private static void AddInventoryHost(string path, IDictionary<string, InventoryHost> hosts, string host)
        {
            if (!string.IsNullOrWhiteSpace(host))
            {
                hosts.TryAdd(host, new(host, $"inventory:{path}"));
            }
        }

        private static void AddInventoryTag(IDictionary<string, List<string>> tags, string tag, string host)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                return;
            }

            if (!tags.TryGetValue(tag, out var hostsForTag))
            {
                hostsForTag = [];
                tags[tag] = hostsForTag;
            }

            hostsForTag.Add(host);
        }

        private static IEnumerable<string> ParseInlineTags(string value) =>
            value.Trim()
                .Trim('[', ']')
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private sealed record InventoryHost(string Name, string Source);
}
