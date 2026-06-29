using Dispatch.Core.Models;
using Dispatch.Core.Validation;

namespace Dispatch.Core.Targeting;

public static class TargetResolver
{
    public static InventoryInspectionResult InspectInventory(string inventoryPath)
    {
        var errors = new List<DispatchValidationError>();
        var inventory = ReadInventory(inventoryPath, errors);
        if (errors.Count > 0)
        {
            return new InventoryInspectionResult(inventoryPath, [], errors);
        }

        var targets = inventory.AllHosts
            .Select(static host => new TargetSpec(host.Name, host.Source))
            .ToArray();
        var inventoryTransportPolicies = inventory.ResolveTransportPoliciesForTargets(targets, errors);
        var inventoryCredentialReferences = inventory.ResolveCredentialReferencesForTargets(targets, errors);
        if (errors.Count > 0)
        {
            return new InventoryInspectionResult(inventoryPath, [], errors);
        }

        var hosts = targets
            .Select(target => new InventoryHostInspection(
                target.Name,
                target.Source,
                inventory.ResolveGroupNamesForHost(target.Name),
                inventoryTransportPolicies.TryGetValue(target.Name, out var transport) ? transport : null,
                inventoryCredentialReferences.TryGetValue(target.Name, out var credentialReference) ? credentialReference : null))
            .OrderBy(static host => host.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new InventoryInspectionResult(inventoryPath, hosts, errors);
    }

    public static InventoryGraphInspectionResult InspectInventoryGraph(string inventoryPath)
    {
        var errors = new List<DispatchValidationError>();
        var inventory = ReadInventory(inventoryPath, errors);
        if (errors.Count > 0)
        {
            return new InventoryGraphInspectionResult(inventoryPath, [], [], [], errors);
        }

        var targets = inventory.AllHosts
            .Select(static host => new TargetSpec(host.Name, host.Source))
            .ToArray();
        var inventoryTransportPolicies = inventory.ResolveTransportPoliciesForTargets(targets, errors);
        var inventoryCredentialReferences = inventory.ResolveCredentialReferencesForTargets(targets, errors);
        if (errors.Count > 0)
        {
            return new InventoryGraphInspectionResult(inventoryPath, [], [], [], errors);
        }

        var hosts = targets
            .Select(target => new InventoryHostInspection(
                target.Name,
                target.Source,
                inventory.ResolveGroupNamesForHost(target.Name),
                inventoryTransportPolicies.TryGetValue(target.Name, out var transport) ? transport : null,
                inventoryCredentialReferences.TryGetValue(target.Name, out var credentialReference) ? credentialReference : null))
            .OrderBy(static host => host.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ungroupedHosts = hosts
            .Where(static host => host.Groups.Count == 0)
            .Select(static host => host.Name)
            .ToArray();

        return new InventoryGraphInspectionResult(
            inventoryPath,
            inventory.CreateGraphGroups(),
            ungroupedHosts,
            hosts,
            errors);
    }

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
            ExcludeSelectorTargets(selector, inventory, targets, errors);
        }

        var inventoryTransportPolicies = errors.Count == 0
            ? inventory.ResolveTransportPoliciesForTargets(targets, errors)
            : new Dictionary<string, TransportKind?>(StringComparer.OrdinalIgnoreCase);
        var inventoryTransport = errors.Count == 0
            ? ResolveCommonInventoryTransport(inventoryTransportPolicies, errors)
            : null;
        var inventoryCredentialReferences = errors.Count == 0
            ? inventory.ResolveCredentialReferencesForTargets(targets, errors)
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<TargetSpec> resolvedTargets = inventoryCredentialReferences.Count > 0
            ? targets
                .Select(target => inventoryCredentialReferences.TryGetValue(target.Name, out var credentialReference)
                    ? target with { CredentialReference = credentialReference }
                    : target)
                .ToArray()
            : targets;

        if (targets.Count == 0 && errors.Count == 0)
        {
            errors.Add(new("TargetsRequired", "At least one target is required from --target, --inventory, --computer-name, or --target-file."));
        }

        return new TargetResolutionResult(resolvedTargets, errors, inventoryTransport, inventoryTransportPolicies, inventoryCredentialReferences);
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
            if (item.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var targetFile = item["file:".Length..].Trim();
                if (targetFile.Length == 0)
                {
                    errors.Add(new("TargetSelectorInvalid", "Target selector 'file:' must include a file path."));
                    continue;
                }

                AddTargetFileTargets(targetFile, targets, seen, errors);
                continue;
            }

            if (IsUnsupportedSelectorExpression(item))
            {
                errors.Add(new("TargetSelectorUnsupported", $"Target selector '{item}' uses unsupported selector expression syntax."));
                continue;
            }

            if (item.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var selected = inventory.AllHosts.ToArray();
                if (selected.Length == 0)
                {
                    errors.Add(new("TargetSelectorMatchedNoTargets", "Target selector 'all' did not match any inventory hosts."));
                    continue;
                }

                foreach (var target in selected)
                {
                    AddTarget(target.Name, target.Source, targets, seen);
                }

                continue;
            }

            if (item.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            {
                var tag = item["tag:".Length..].Trim();
                if (tag.Length == 0)
                {
                    errors.Add(new("TargetSelectorInvalid", "Target selector 'tag:' must include a tag name."));
                    continue;
                }

                var selected = inventory.HostsByTag(tag).ToArray();
                if (selected.Length == 0)
                {
                    errors.Add(new("TargetSelectorMatchedNoTargets", $"Target selector 'tag:{tag}' did not match any inventory hosts."));
                    continue;
                }

                foreach (var target in selected)
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
        ICollection<TargetSpec> targets,
        ICollection<DispatchValidationError> errors)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in SplitTargets(selector))
        {
            if (item.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var targetFile = item["file:".Length..].Trim();
                if (targetFile.Length == 0)
                {
                    errors.Add(new("TargetSelectorInvalid", "Exclude selector 'file:' must include a file path."));
                    continue;
                }

                var fileTargets = new List<TargetSpec>();
                AddTargetFileTargets(targetFile, fileTargets, new HashSet<string>(StringComparer.OrdinalIgnoreCase), errors);
                foreach (var target in fileTargets)
                {
                    excluded.Add(target.Name);
                }

                continue;
            }

            if (IsUnsupportedSelectorExpression(item))
            {
                errors.Add(new("TargetSelectorUnsupported", $"Exclude selector '{item}' uses unsupported selector expression syntax."));
                continue;
            }

            if (item.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var selected = inventory.AllHosts.ToArray();
                if (selected.Length == 0)
                {
                    errors.Add(new("TargetSelectorMatchedNoTargets", "Exclude selector 'all' did not match any inventory hosts."));
                    continue;
                }

                foreach (var target in selected)
                {
                    excluded.Add(target.Name);
                }

                continue;
            }

            if (item.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            {
                var tag = item["tag:".Length..].Trim();
                if (tag.Length == 0)
                {
                    errors.Add(new("TargetSelectorInvalid", "Exclude selector 'tag:' must include a tag name."));
                    continue;
                }

                var selected = inventory.HostsByTag(tag).ToArray();
                if (selected.Length == 0)
                {
                    errors.Add(new("TargetSelectorMatchedNoTargets", $"Exclude selector 'tag:{tag}' did not match any inventory hosts."));
                    continue;
                }

                foreach (var target in selected)
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
            ? InventoryTargets.FromYaml(inventoryPath, lines, errors)
            : InventoryTargets.FromText(inventoryPath, lines);
    }

    private static bool LooksLikeYaml(IEnumerable<string> lines) =>
        lines.Any(static line =>
        {
            var withoutComments = line.Split('#', 2)[0].TrimEnd();
            var trimmed = withoutComments.Trim();
            if (withoutComments.Length == trimmed.Length
                && TryParseInlineMap(trimmed, "defaults", out _))
            {
                return true;
            }

            if (withoutComments.Length == trimmed.Length
                && TryParseInlineTopLevelHostsSection(trimmed, out _))
            {
                return true;
            }

            if (!trimmed.EndsWith(':'))
            {
                return trimmed.StartsWith("tags:", StringComparison.OrdinalIgnoreCase);
            }

            var sectionName = trimmed.TrimEnd(':');
            return sectionName.Equals("defaults", StringComparison.OrdinalIgnoreCase)
                || sectionName.Equals("groups", StringComparison.OrdinalIgnoreCase)
                || sectionName.Equals("hosts", StringComparison.OrdinalIgnoreCase);
        });

    private static bool IsUnsupportedSelectorExpression(string selector) =>
        selector.Equals("!", StringComparison.Ordinal) ||
        selector.StartsWith('!') ||
        selector.Contains('&', StringComparison.Ordinal) ||
        selector.Contains(":!", StringComparison.Ordinal) ||
        selector.Contains(":&", StringComparison.Ordinal);

    private static IEnumerable<string> SplitTargets(string value) =>
        value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(static target => !string.IsNullOrWhiteSpace(target));

    private static bool TryParseInlineTopLevelHostsSection(string value, out IReadOnlyList<string> hosts)
    {
        hosts = [];
        if (!value.StartsWith("hosts:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var assigned = value["hosts:".Length..].Trim();
        if (!assigned.StartsWith('[') || !assigned.EndsWith(']'))
        {
            return false;
        }

        hosts = assigned
            .Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return true;
    }

    private static bool TryParseInlineMap(
        string value,
        string fieldName,
        out string innerValue)
    {
        innerValue = string.Empty;
        var prefix = $"{fieldName}:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var assigned = value[prefix.Length..].Trim();
        if (!assigned.StartsWith("{", StringComparison.Ordinal) || !assigned.EndsWith("}", StringComparison.Ordinal))
        {
            return false;
        }

        innerValue = assigned[1..^1].Trim();
        return true;
    }

    private static TransportKind? ResolveCommonInventoryTransport(
        IReadOnlyDictionary<string, TransportKind?> inventoryTransportPolicies,
        ICollection<DispatchValidationError> errors)
    {
        TransportKind? resolvedTransport = null;

        foreach (var policy in inventoryTransportPolicies.Values)
        {
            if (policy is null)
            {
                continue;
            }

            if (resolvedTransport is null)
            {
                resolvedTransport = policy.Value;
                continue;
            }

            if (resolvedTransport != policy.Value)
            {
                errors.Add(new(
                    "InventoryTransportConflict",
                    $"Selected targets resolved conflicting inventory transport policies '{resolvedTransport.Value.ToDispatchString()}' and '{policy.Value.ToDispatchString()}'. Use --transport to override or align the inventory transport settings."));
                return null;
            }
        }

        return resolvedTransport;
    }

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
        private readonly Dictionary<string, InventoryGroup> groups;
        private readonly Dictionary<string, List<string>> tags;
        private readonly Dictionary<string, TransportKind> groupTransports;
        private readonly Dictionary<string, string> groupCredentialReferences;
        private readonly TransportKind? defaultTransport;
        private readonly string? defaultCredentialReference;

        private InventoryTargets(
            Dictionary<string, InventoryHost> hosts,
            Dictionary<string, InventoryGroup> groups,
            Dictionary<string, List<string>> tags,
            Dictionary<string, TransportKind> groupTransports,
            Dictionary<string, string> groupCredentialReferences,
            TransportKind? defaultTransport,
            string? defaultCredentialReference)
        {
            this.hosts = hosts;
            this.groups = groups;
            this.tags = tags;
            this.groupTransports = groupTransports;
            this.groupCredentialReferences = groupCredentialReferences;
            this.defaultTransport = defaultTransport;
            this.defaultCredentialReference = defaultCredentialReference;
        }

        public static InventoryTargets Empty { get; } = new(
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            null,
            null);

        public IEnumerable<InventoryHost> AllHosts => hosts.Values;

        public IReadOnlyList<InventoryGraphGroup> CreateGraphGroups() =>
            groups
                .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new InventoryGraphGroup(
                    group.Key,
                    group.Value.Hosts.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    group.Value.Children.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    groupTransports.TryGetValue(group.Key, out var transport) ? transport : null,
                    groupCredentialReferences.TryGetValue(group.Key, out var credentialReference) ? credentialReference : null))
                .ToArray();

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
                    hosts.TryAdd(target, new(target, $"inventory:{path}:{lineNumber}", null, null));
                }
            }

            return new(
                hosts,
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase),
                null,
                null);
        }

        public static InventoryTargets FromYaml(string path, IReadOnlyList<string> lines, ICollection<DispatchValidationError> errors)
        {
            var hosts = new Dictionary<string, InventoryHost>(StringComparer.OrdinalIgnoreCase);
            var groups = new Dictionary<string, InventoryGroup>(StringComparer.OrdinalIgnoreCase);
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var groupTransports = new Dictionary<string, TransportKind>(StringComparer.OrdinalIgnoreCase);
            var groupCredentialReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            TransportKind? defaultTransport = null;
            string? defaultCredentialReference = null;
            string? section = null;
            string? currentGroup = null;
            string? currentHost = null;
            var inGroupHosts = false;
            var inGroupChildren = false;
            var inGroupVars = false;
            var inHostTags = false;
            var inHostVars = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Split('#', 2)[0].TrimEnd();
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var indent = line.Length - line.TrimStart().Length;
                if (indent == 0 && TryParseInlineMap(trimmed, "defaults", out var inlineDefaultsValue))
                {
                    section = "defaults";
                    currentGroup = null;
                    currentHost = null;
                    inGroupHosts = false;
                    inGroupChildren = false;
                    inGroupVars = false;
                    inHostTags = false;
                    inHostVars = false;

                    ApplyInlineDefaultsMap(
                        inlineDefaultsValue,
                        errors,
                        transport => defaultTransport = transport,
                        credential => defaultCredentialReference = credential);
                    continue;
                }

                if (indent == 0 && TryParseInlineTopLevelHostsSection(trimmed, out var inlineTopLevelHosts))
                {
                    section = "hosts";
                    currentGroup = null;
                    currentHost = null;
                    inGroupHosts = false;
                    inGroupChildren = false;
                    inGroupVars = false;
                    inHostTags = false;
                    inHostVars = false;

                    foreach (var host in inlineTopLevelHosts)
                    {
                        AddInventoryHost(path, hosts, host);
                    }

                    continue;
                }

                if (indent == 0 && trimmed.EndsWith(':'))
                {
                    var sectionName = trimmed.TrimEnd(':');
                    if (!IsSupportedTopLevelSection(sectionName))
                    {
                        errors.Add(new("InventorySectionUnsupported", $"Inventory section '{sectionName}' is not supported."));
                        section = null;
                        currentGroup = null;
                        currentHost = null;
                        inGroupHosts = false;
                        inGroupChildren = false;
                        inGroupVars = false;
                        inHostTags = false;
                        inHostVars = false;
                        continue;
                    }

                    section = sectionName;
                    currentGroup = null;
                    currentHost = null;
                    inGroupHosts = false;
                    inGroupChildren = false;
                    inGroupVars = false;
                    inHostTags = false;
                    inHostVars = false;
                    continue;
                }

                if (indent == 0)
                {
                    errors.Add(new("InventorySchemaInvalid", $"Inventory line '{trimmed}' is not a supported top-level mapping."));
                    continue;
                }

                if (section?.Equals("defaults", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (indent == 2 && TryParseTransportAssignment(trimmed, out var parsedTransport))
                    {
                        if (parsedTransport is null)
                        {
                            errors.Add(new("InventoryTransportInvalid", $"Inventory default transport '{ReadAssignedValue(trimmed)}' is not supported."));
                            continue;
                        }

                        defaultTransport = parsedTransport.Value;
                        continue;
                    }

                    if (TryApplyCredentialReferenceAssignment(
                        trimmed,
                        errors,
                        "Inventory defaults",
                        credential => defaultCredentialReference = credential))
                    {
                        continue;
                    }

                    if (TryRejectPlaintextSecretField(trimmed, errors, "Inventory defaults"))
                    {
                        continue;
                    }

                    errors.Add(new("InventoryFieldUnsupported", $"Inventory defaults field '{ReadFieldName(trimmed)}' is not supported."));
                    continue;
                }

                if (section?.Equals("groups", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (indent == 2 && trimmed.EndsWith(':'))
                    {
                        currentGroup = trimmed.TrimEnd(':');
                        groups.TryAdd(currentGroup, new InventoryGroup([], []));
                        inGroupHosts = false;
                        inGroupChildren = false;
                        inGroupVars = false;
                        continue;
                    }

                    if (indent >= 4 && trimmed.Equals("hosts:", StringComparison.OrdinalIgnoreCase))
                    {
                        inGroupHosts = currentGroup is not null;
                        inGroupChildren = false;
                        inGroupVars = false;
                        continue;
                    }

                    if (currentGroup is not null
                        && indent >= 4
                        && TryParseInlineGroupMembers(trimmed, "hosts", out var inlineHosts))
                    {
                        foreach (var host in inlineHosts)
                        {
                            AddInventoryGroupHost(path, hosts, groups, currentGroup, host);
                        }

                        inGroupHosts = false;
                        inGroupChildren = false;
                        inGroupVars = false;
                        continue;
                    }

                    if (indent >= 4 && trimmed.Equals("children:", StringComparison.OrdinalIgnoreCase))
                    {
                        inGroupChildren = currentGroup is not null;
                        inGroupHosts = false;
                        inGroupVars = false;
                        continue;
                    }

                    if (currentGroup is not null
                        && indent >= 4
                        && TryParseInlineGroupMembers(trimmed, "children", out var inlineChildren))
                    {
                        foreach (var child in inlineChildren)
                        {
                            AddInventoryGroupChild(groups, currentGroup, child);
                        }

                        inGroupChildren = false;
                        inGroupHosts = false;
                        inGroupVars = false;
                        continue;
                    }

                    if (currentGroup is not null
                        && indent >= 4
                        && TryParseInlineMap(trimmed, "vars", out var inlineGroupVars))
                    {
                        inGroupVars = false;
                        inGroupHosts = false;
                        inGroupChildren = false;

                        ApplyInlineGroupVarsMap(
                            inlineGroupVars,
                            errors,
                            currentGroup,
                            transport => groupTransports[currentGroup] = transport,
                            credential => groupCredentialReferences[currentGroup] = credential);
                        continue;
                    }

                    if (indent >= 4 && trimmed.Equals("vars:", StringComparison.OrdinalIgnoreCase))
                    {
                        inGroupVars = currentGroup is not null;
                        inGroupHosts = false;
                        inGroupChildren = false;
                        continue;
                    }

                    if (inGroupHosts && trimmed.StartsWith("- ", StringComparison.Ordinal))
                    {
                        AddInventoryGroupHost(path, hosts, groups, currentGroup!, trimmed[2..].Trim());
                        continue;
                    }

                    if (inGroupHosts && indent >= 6 && trimmed.EndsWith(':'))
                    {
                        AddInventoryGroupHost(path, hosts, groups, currentGroup!, trimmed.TrimEnd(':').Trim());
                        continue;
                    }

                    if (inGroupChildren && trimmed.StartsWith("- ", StringComparison.Ordinal))
                    {
                        AddInventoryGroupChild(groups, currentGroup!, trimmed[2..].Trim());
                        continue;
                    }

                    if (inGroupChildren && indent >= 6 && trimmed.EndsWith(':'))
                    {
                        AddInventoryGroupChild(groups, currentGroup!, trimmed.TrimEnd(':').Trim());
                        continue;
                    }

                    if (currentGroup is not null && indent >= 4 && TryParseTransportAssignment(trimmed, out var parsedGroupTransport))
                    {
                        if (parsedGroupTransport is null)
                        {
                            errors.Add(new("InventoryTransportInvalid", $"Group '{currentGroup}' has unsupported transport '{ReadAssignedValue(trimmed)}'."));
                            continue;
                        }

                        if (inGroupVars || indent == 4)
                        {
                            groupTransports[currentGroup] = parsedGroupTransport.Value;
                        }

                        continue;
                    }

                    if (currentGroup is not null
                        && indent >= 4
                        && TryApplyCredentialReferenceAssignment(
                            trimmed,
                            errors,
                            $"Group '{currentGroup}'",
                            credential => groupCredentialReferences[currentGroup] = credential))
                    {
                        continue;
                    }

                    if (currentGroup is null)
                    {
                        errors.Add(new("InventorySchemaInvalid", $"Inventory groups entry '{trimmed}' appears before a group name."));
                        continue;
                    }

                    if (inGroupHosts)
                    {
                        errors.Add(new("InventoryFieldUnsupported", $"Group '{currentGroup}' only supports host list entries under 'hosts:'."));
                        continue;
                    }

                    if (inGroupChildren)
                    {
                        errors.Add(new("InventoryFieldUnsupported", $"Group '{currentGroup}' only supports group list entries under 'children:'."));
                        continue;
                    }

                    if (inGroupVars)
                    {
                        if (TryRejectPlaintextSecretField(trimmed, errors, $"Group '{currentGroup}' var"))
                        {
                            continue;
                        }

                        errors.Add(new("InventoryFieldUnsupported", $"Group '{currentGroup}' var '{ReadFieldName(trimmed)}' is not supported."));
                        continue;
                    }

                    if (indent >= 4 && TryReadFieldName(trimmed, out var groupFieldName))
                    {
                        if (IsPlaintextSecretFieldName(groupFieldName))
                        {
                            AddSecretFieldError(errors, $"Group '{currentGroup}'", groupFieldName);
                            continue;
                        }

                        errors.Add(new("InventoryFieldUnsupported", $"Group '{currentGroup}' field '{groupFieldName}' is not supported."));
                        continue;
                    }

                    errors.Add(new("InventorySchemaInvalid", $"Group '{currentGroup}' entry '{trimmed}' is not supported."));
                    continue;
                }

                if (section?.Equals("hosts", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (indent == 2 && trimmed.StartsWith("- ", StringComparison.Ordinal))
                    {
                        currentHost = trimmed[2..].Trim();
                        AddInventoryHost(path, hosts, currentHost);
                        inHostTags = false;
                        inHostVars = false;
                        continue;
                    }

                    if (indent == 2 && TryParseInlineNamedMapEntry(trimmed, out var inlineHostName, out var inlineHostMap))
                    {
                        currentHost = inlineHostName;
                        AddInventoryHost(path, hosts, currentHost);
                        inHostTags = false;
                        inHostVars = false;
                        ApplyInlineHostMap(hosts, tags, errors, currentHost, inlineHostMap);
                        continue;
                    }

                    if (indent == 2 && trimmed.EndsWith(':'))
                    {
                        currentHost = trimmed.TrimEnd(':');
                        AddInventoryHost(path, hosts, currentHost);
                        inHostTags = false;
                        inHostVars = false;
                        continue;
                    }

                    if (currentHost is not null && trimmed.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var tag in ParseInlineTags(trimmed["tags:".Length..]))
                        {
                            AddInventoryTag(tags, tag, currentHost);
                        }

                        inHostTags = true;
                        inHostVars = false;
                        continue;
                    }

                    if (currentHost is not null && inHostTags && trimmed.StartsWith("- ", StringComparison.Ordinal))
                    {
                        AddInventoryTag(tags, trimmed[2..].Trim(), currentHost);
                        continue;
                    }

                    if (currentHost is not null && indent >= 4 && TryParseInlineMap(trimmed, "vars", out var inlineHostVars))
                    {
                        inHostVars = false;
                        inHostTags = false;

                        ApplyInlineHostVarsMap(
                            hosts,
                            errors,
                            currentHost,
                            inlineHostVars);
                        continue;
                    }

                    if (currentHost is not null && indent >= 4 && trimmed.Equals("vars:", StringComparison.OrdinalIgnoreCase))
                    {
                        inHostVars = true;
                        inHostTags = false;
                        continue;
                    }

                    if (currentHost is not null && indent >= 4 && TryParseTransportAssignment(trimmed, out var parsedHostTransport))
                    {
                        if (parsedHostTransport is null)
                        {
                            errors.Add(new("InventoryTransportInvalid", $"Host '{currentHost}' has unsupported transport '{ReadAssignedValue(trimmed)}'."));
                            continue;
                        }

                        if (indent == 4 || inHostVars)
                        {
                            hosts[currentHost] = hosts[currentHost] with { Transport = parsedHostTransport };
                        }

                        continue;
                    }

                    if (currentHost is not null
                        && indent >= 4
                        && TryApplyCredentialReferenceAssignment(
                            trimmed,
                            errors,
                            $"Host '{currentHost}'",
                            credential => hosts[currentHost] = hosts[currentHost] with { CredentialReference = credential }))
                    {
                        continue;
                    }

                    if (currentHost is null)
                    {
                        errors.Add(new("InventorySchemaInvalid", $"Inventory host entry '{trimmed}' appears before a host name."));
                        continue;
                    }

                    if (inHostTags)
                    {
                        errors.Add(new("InventoryFieldUnsupported", $"Host '{currentHost}' only supports tag list entries under 'tags:'."));
                        continue;
                    }

                    if (inHostVars)
                    {
                        if (TryRejectPlaintextSecretField(trimmed, errors, $"Host '{currentHost}' var"))
                        {
                            continue;
                        }

                        errors.Add(new("InventoryFieldUnsupported", $"Host '{currentHost}' var '{ReadFieldName(trimmed)}' is not supported."));
                        continue;
                    }

                    if (indent >= 4 && TryReadFieldName(trimmed, out var hostFieldName))
                    {
                        if (IsPlaintextSecretFieldName(hostFieldName))
                        {
                            AddSecretFieldError(errors, $"Host '{currentHost}'", hostFieldName);
                            continue;
                        }

                        errors.Add(new("InventoryFieldUnsupported", $"Host '{currentHost}' field '{hostFieldName}' is not supported."));
                        continue;
                    }

                    errors.Add(new("InventorySchemaInvalid", $"Host '{currentHost}' entry '{trimmed}' is not supported."));
                }
            }

            ValidateGroupGraph(groups, errors);
            return new(hosts, groups, tags, groupTransports, groupCredentialReferences, defaultTransport, defaultCredentialReference);
        }

        public bool ContainsGroupOrHost(string selector) =>
            hosts.ContainsKey(selector) || groups.ContainsKey(selector);

        public IEnumerable<InventoryHost> ResolveGroupOrHost(string selector)
        {
            if (hosts.TryGetValue(selector, out var host))
            {
                yield return host;
            }

            if (!groups.TryGetValue(selector, out _))
            {
                yield break;
            }

            foreach (var groupHost in ResolveGroupHosts(selector))
            {
                yield return groupHost;
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

        public IReadOnlyDictionary<string, TransportKind?> ResolveTransportPoliciesForTargets(
            IEnumerable<TargetSpec> targets,
            ICollection<DispatchValidationError> errors)
        {
            var resolvedPolicies = new Dictionary<string, TransportKind?>(StringComparer.OrdinalIgnoreCase);

            foreach (var target in targets)
            {
                if (!TryResolveTransportForHost(target.Name, errors, out var hostTransport) || hostTransport is null)
                {
                    continue;
                }

                resolvedPolicies[target.Name] = hostTransport.Value;
            }

            return resolvedPolicies;
        }

        public IReadOnlyDictionary<string, string?> ResolveCredentialReferencesForTargets(
            IEnumerable<TargetSpec> targets,
            ICollection<DispatchValidationError> errors)
        {
            var resolvedReferences = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var target in targets)
            {
                if (!TryResolveCredentialReferenceForHost(target.Name, errors, out var credentialReference)
                    || string.IsNullOrWhiteSpace(credentialReference))
                {
                    continue;
                }

                resolvedReferences[target.Name] = credentialReference;
            }

            return resolvedReferences;
        }

        private bool TryResolveTransportForHost(
            string hostName,
            ICollection<DispatchValidationError> errors,
            out TransportKind? transport)
        {
            transport = null;

            if (!hosts.TryGetValue(hostName, out var host))
            {
                return false;
            }

            if (host.Transport is not null)
            {
                transport = host.Transport.Value;
                return true;
            }

            TransportKind? groupTransport = null;
            foreach (var groupName in ResolveGroupsForHost(hostName))
            {
                if (!groupTransports.TryGetValue(groupName, out var candidateTransport))
                {
                    continue;
                }

                if (groupTransport is null)
                {
                    groupTransport = candidateTransport;
                    continue;
                }

                if (groupTransport != candidateTransport)
                {
                    errors.Add(new(
                        "InventoryTransportConflict",
                        $"Host '{hostName}' inherits conflicting group transport policies '{groupTransport.Value.ToDispatchString()}' and '{candidateTransport.ToDispatchString()}'."));
                    return false;
                }
            }

            if (groupTransport is not null)
            {
                transport = groupTransport.Value;
                return true;
            }

            if (defaultTransport is not null)
            {
                transport = defaultTransport.Value;
                return true;
            }

            return false;
        }

        private bool TryResolveCredentialReferenceForHost(
            string hostName,
            ICollection<DispatchValidationError> errors,
            out string? credentialReference)
        {
            credentialReference = null;

            if (!hosts.TryGetValue(hostName, out var host))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(host.CredentialReference))
            {
                credentialReference = host.CredentialReference;
                return true;
            }

            string? groupCredentialReference = null;
            foreach (var groupName in ResolveGroupsForHost(hostName))
            {
                if (!groupCredentialReferences.TryGetValue(groupName, out var candidateReference))
                {
                    continue;
                }

                if (groupCredentialReference is null)
                {
                    groupCredentialReference = candidateReference;
                    continue;
                }

                if (!groupCredentialReference.Equals(candidateReference, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new(
                        "InventoryCredentialConflict",
                        $"Host '{hostName}' inherits conflicting group credential references '{groupCredentialReference}' and '{candidateReference}'."));
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(groupCredentialReference))
            {
                credentialReference = groupCredentialReference;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(defaultCredentialReference))
            {
                credentialReference = defaultCredentialReference;
                return true;
            }

            return false;
        }

        private static void AddInventoryGroupHost(
            string path,
            IDictionary<string, InventoryHost> hosts,
            IDictionary<string, InventoryGroup> groups,
            string group,
            string host)
        {
            AddInventoryHost(path, hosts, host);
            if (string.IsNullOrWhiteSpace(host))
            {
                return;
            }

            groups[group].Hosts.Add(host);
        }

        private static void AddInventoryGroupChild(
            IDictionary<string, InventoryGroup> groups,
            string group,
            string child)
        {
            if (string.IsNullOrWhiteSpace(child))
            {
                return;
            }

            groups[group].Children.Add(child);
        }

        private static void AddInventoryHost(string path, IDictionary<string, InventoryHost> hosts, string host)
        {
            if (!string.IsNullOrWhiteSpace(host))
            {
                hosts.TryAdd(host, new(host, $"inventory:{path}", null, null));
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

        private static void ApplyInlineHostMap(
            IDictionary<string, InventoryHost> hosts,
            IDictionary<string, List<string>> tags,
            ICollection<DispatchValidationError> errors,
            string hostName,
            string inlineHostMap)
        {
            foreach (var field in SplitInlineMapFields(inlineHostMap))
            {
                if (field.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var tag in ParseInlineTags(field["tags:".Length..]))
                    {
                        AddInventoryTag(tags, tag, hostName);
                    }

                    continue;
                }

                if (TryApplyCredentialReferenceAssignment(
                    field,
                    errors,
                    $"Host '{hostName}'",
                    credential => hosts[hostName] = hosts[hostName] with { CredentialReference = credential }))
                {
                    continue;
                }

                if (TryParseInlineMap(field, "vars", out var inlineHostVars))
                {
                    ApplyInlineHostVarsMap(hosts, errors, hostName, inlineHostVars);
                    continue;
                }

                if (TryRejectPlaintextSecretField(field, errors, $"Host '{hostName}'"))
                {
                    continue;
                }

                errors.Add(new("InventoryFieldUnsupported", $"Host '{hostName}' field '{ReadFieldName(field)}' is not supported."));
            }
        }

        private static void ApplyInlineDefaultsMap(
            string inlineDefaultsMap,
            ICollection<DispatchValidationError> errors,
            Action<TransportKind> applyTransport,
            Action<string> applyCredentialReference)
        {
            foreach (var field in SplitInlineMapFields(inlineDefaultsMap))
            {
                if (TryParseTransportAssignment(field, out var parsedTransport))
                {
                    if (parsedTransport is null)
                    {
                        errors.Add(new("InventoryTransportInvalid", $"Inventory default transport '{ReadAssignedValue(field)}' is not supported."));
                        continue;
                    }

                    applyTransport(parsedTransport.Value);
                    continue;
                }

                if (TryApplyCredentialReferenceAssignment(field, errors, "Inventory defaults", applyCredentialReference))
                {
                    continue;
                }

                if (TryRejectPlaintextSecretField(field, errors, "Inventory defaults"))
                {
                    continue;
                }

                errors.Add(new("InventoryFieldUnsupported", $"Inventory defaults field '{ReadFieldName(field)}' is not supported."));
            }
        }

        private static void ApplyInlineGroupVarsMap(
            string inlineGroupVars,
            ICollection<DispatchValidationError> errors,
            string groupName,
            Action<TransportKind> applyTransport,
            Action<string> applyCredentialReference)
        {
            foreach (var field in SplitInlineMapFields(inlineGroupVars))
            {
                if (TryParseTransportAssignment(field, out var parsedTransport))
                {
                    if (parsedTransport is null)
                    {
                        errors.Add(new("InventoryTransportInvalid", $"Group '{groupName}' has unsupported transport '{ReadAssignedValue(field)}'."));
                        continue;
                    }

                    applyTransport(parsedTransport.Value);
                    continue;
                }

                if (TryApplyCredentialReferenceAssignment(field, errors, $"Group '{groupName}' var", applyCredentialReference))
                {
                    continue;
                }

                if (TryRejectPlaintextSecretField(field, errors, $"Group '{groupName}' var"))
                {
                    continue;
                }

                errors.Add(new("InventoryFieldUnsupported", $"Group '{groupName}' var '{ReadFieldName(field)}' is not supported."));
            }
        }

        private static void ApplyInlineHostVarsMap(
            IDictionary<string, InventoryHost> hosts,
            ICollection<DispatchValidationError> errors,
            string hostName,
            string inlineHostVars)
        {
            foreach (var field in SplitInlineMapFields(inlineHostVars))
            {
                if (TryParseTransportAssignment(field, out var parsedTransport))
                {
                    if (parsedTransport is null)
                    {
                        errors.Add(new("InventoryTransportInvalid", $"Host '{hostName}' has unsupported transport '{ReadAssignedValue(field)}'."));
                        continue;
                    }

                    hosts[hostName] = hosts[hostName] with { Transport = parsedTransport.Value };
                    continue;
                }

                if (TryApplyCredentialReferenceAssignment(
                    field,
                    errors,
                    $"Host '{hostName}' var",
                    credential => hosts[hostName] = hosts[hostName] with { CredentialReference = credential }))
                {
                    continue;
                }

                if (TryRejectPlaintextSecretField(field, errors, $"Host '{hostName}' var"))
                {
                    continue;
                }

                errors.Add(new("InventoryFieldUnsupported", $"Host '{hostName}' var '{ReadFieldName(field)}' is not supported."));
            }
        }

        private static bool TryParseInlineGroupMembers(
            string value,
            string fieldName,
            out IReadOnlyList<string> members)
        {
            members = [];
            var prefix = $"{fieldName}:";
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var assigned = value[prefix.Length..].Trim();
            if (!assigned.StartsWith("[", StringComparison.Ordinal) || !assigned.EndsWith("]", StringComparison.Ordinal))
            {
                return false;
            }

            members = assigned
                .Trim('[', ']')
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return true;
        }

        private static bool TryParseInlineNamedMapEntry(
            string value,
            out string name,
            out string mapValue)
        {
            name = string.Empty;
            mapValue = string.Empty;

            var separatorIndex = value.IndexOf(':', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                return false;
            }

            name = value[..separatorIndex].Trim();
            if (name.Length == 0)
            {
                return false;
            }

            var assigned = value[(separatorIndex + 1)..].Trim();
            if (!assigned.StartsWith("{", StringComparison.Ordinal) || !assigned.EndsWith("}", StringComparison.Ordinal))
            {
                return false;
            }

            mapValue = assigned[1..^1].Trim();
            return true;
        }

        private static IEnumerable<string> SplitInlineMapFields(string value)
        {
            var fields = new List<string>();
            var start = 0;
            var bracketDepth = 0;
            var braceDepth = 0;

            for (var index = 0; index < value.Length; index++)
            {
                switch (value[index])
                {
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        braceDepth--;
                        break;
                    case ',' when bracketDepth == 0 && braceDepth == 0:
                        AddInlineMapField(fields, value[start..index]);
                        start = index + 1;
                        break;
                }
            }

            AddInlineMapField(fields, value[start..]);
            return fields;
        }

        private static void AddInlineMapField(ICollection<string> fields, string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length > 0)
            {
                fields.Add(trimmed);
            }
        }

        private static bool IsSupportedTopLevelSection(string sectionName) =>
            sectionName.Equals("defaults", StringComparison.OrdinalIgnoreCase)
            || sectionName.Equals("groups", StringComparison.OrdinalIgnoreCase)
            || sectionName.Equals("hosts", StringComparison.OrdinalIgnoreCase);

        private static bool TryParseTransportAssignment(string value, out TransportKind? transport)
        {
            transport = null;
            if (!value.StartsWith("transport:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var assigned = ReadAssignedValue(value);
            if (assigned.Equals("psexec", StringComparison.OrdinalIgnoreCase))
            {
                transport = TransportKind.PsExec;
            }
            else if (assigned.Equals("psrp", StringComparison.OrdinalIgnoreCase))
            {
                transport = TransportKind.Psrp;
            }
            else if (assigned.Equals("winrm", StringComparison.OrdinalIgnoreCase))
            {
                transport = TransportKind.WinRm;
            }

            return true;
        }

        private static bool TryParseCredentialReferenceAssignment(string value, out string? credentialReference)
        {
            credentialReference = null;
            if (!value.StartsWith("credential:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            credentialReference = ReadAssignedValue(value);
            return true;
        }

        private static bool TryApplyCredentialReferenceAssignment(
            string value,
            ICollection<DispatchValidationError> errors,
            string context,
            Action<string> applyCredentialReference)
        {
            if (!TryParseCredentialReferenceAssignment(value, out var credentialReference))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(credentialReference))
            {
                errors.Add(new("InventoryCredentialInvalid", $"{context} credential reference must not be empty."));
                return true;
            }

            applyCredentialReference(credentialReference.Trim());
            return true;
        }

        private static bool TryRejectPlaintextSecretField(
            string value,
            ICollection<DispatchValidationError> errors,
            string context)
        {
            if (!TryReadFieldName(value, out var fieldName) || !IsPlaintextSecretFieldName(fieldName))
            {
                return false;
            }

            AddSecretFieldError(errors, context, fieldName);
            return true;
        }

        private static bool IsPlaintextSecretFieldName(string fieldName)
        {
            var normalized = fieldName.Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal);

            return normalized.Equals("password", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("pass", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("secret", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("token", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("sas", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("sastoken", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("password", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("secret", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("token", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddSecretFieldError(
            ICollection<DispatchValidationError> errors,
            string context,
            string fieldName) =>
            errors.Add(new(
                "InventorySecretFieldUnsupported",
                $"{context} field '{fieldName}' looks like a plaintext secret. Use 'credential: <name>' to reference a credential instead."));

        private static bool TryReadFieldName(string value, out string fieldName)
        {
            fieldName = string.Empty;
            var separatorIndex = value.IndexOf(':', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                return false;
            }

            fieldName = value[..separatorIndex].Trim();
            return fieldName.Length > 0;
        }

        private static string ReadFieldName(string value) =>
            TryReadFieldName(value, out var fieldName)
                ? fieldName
                : value.Trim();

        private static string ReadAssignedValue(string value)
        {
            var separatorIndex = value.IndexOf(':', StringComparison.Ordinal);
            return separatorIndex >= 0
                ? value[(separatorIndex + 1)..].Trim()
                : string.Empty;
        }

        private IEnumerable<InventoryHost> ResolveGroupHosts(string groupName)
        {
            var yieldedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hostName in ResolveGroupHostNames(groupName, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            {
                if (!yieldedHosts.Add(hostName))
                {
                    continue;
                }

                if (hosts.TryGetValue(hostName, out var resolved))
                {
                    yield return resolved;
                }
            }
        }

        private IEnumerable<string> ResolveGroupHostNames(string groupName, ISet<string> visitedGroups)
        {
            if (!visitedGroups.Add(groupName) || !groups.TryGetValue(groupName, out var group))
            {
                yield break;
            }

            foreach (var hostName in group.Hosts)
            {
                yield return hostName;
            }

            foreach (var childGroup in group.Children)
            {
                foreach (var hostName in ResolveGroupHostNames(childGroup, visitedGroups))
                {
                    yield return hostName;
                }
            }
        }

        private IEnumerable<string> ResolveGroupsForHost(string hostName)
        {
            foreach (var groupName in groups.Keys)
            {
                if (GroupContainsHost(groupName, hostName, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                {
                    yield return groupName;
                }
            }
        }

        public IReadOnlyList<string> ResolveGroupNamesForHost(string hostName) =>
            ResolveGroupsForHost(hostName)
                .OrderBy(static groupName => groupName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private bool GroupContainsHost(string groupName, string hostName, ISet<string> visitedGroups)
        {
            if (!visitedGroups.Add(groupName) || !groups.TryGetValue(groupName, out var group))
            {
                return false;
            }

            if (group.Hosts.Contains(hostName, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (var childGroup in group.Children)
            {
                if (GroupContainsHost(childGroup, hostName, visitedGroups))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ValidateGroupGraph(
            IReadOnlyDictionary<string, InventoryGroup> groups,
            ICollection<DispatchValidationError> errors)
        {
            foreach (var group in groups)
            {
                foreach (var child in group.Value.Children)
                {
                    if (!groups.ContainsKey(child))
                    {
                        errors.Add(new("InventorySchemaInvalid", $"Group '{group.Key}' references unknown child group '{child}'."));
                    }
                }
            }

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var activePath = new List<string>();
            var activeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var groupName in groups.Keys)
            {
                ValidateGroupAcyclic(groupName, groups, errors, visited, activePath, activeSet);
            }
        }

        private static void ValidateGroupAcyclic(
            string groupName,
            IReadOnlyDictionary<string, InventoryGroup> groups,
            ICollection<DispatchValidationError> errors,
            ISet<string> visited,
            IList<string> activePath,
            ISet<string> activeSet)
        {
            if (visited.Contains(groupName))
            {
                return;
            }

            if (activeSet.Contains(groupName))
            {
                var cycleStart = activePath.IndexOf(groupName);
                var cycle = cycleStart >= 0
                    ? string.Join(" -> ", activePath.Skip(cycleStart).Append(groupName))
                    : groupName;
                errors.Add(new("InventoryGroupCycle", $"Inventory group children contain a cycle: {cycle}."));
                return;
            }

            if (!groups.TryGetValue(groupName, out var group))
            {
                return;
            }

            activeSet.Add(groupName);
            activePath.Add(groupName);

            foreach (var child in group.Children)
            {
                ValidateGroupAcyclic(child, groups, errors, visited, activePath, activeSet);
            }

            activePath.RemoveAt(activePath.Count - 1);
            activeSet.Remove(groupName);
            visited.Add(groupName);
        }
    }

    private sealed record InventoryHost(string Name, string Source, TransportKind? Transport, string? CredentialReference);
    private sealed record InventoryGroup(List<string> Hosts, List<string> Children);
}
