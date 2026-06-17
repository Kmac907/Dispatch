using Dispatch.Core.Targeting;
using Dispatch.Core.Models;

namespace Dispatch.Core.Tests;

public sealed class TargetResolutionTests
{
    [Fact]
    public void ResolvesCommaSeparatedComputerNamesInFirstSeenOrder()
    {
        var result = TargetResolver.Resolve(new TargetResolutionInput([" PC001,PC002, pc001 ,PC003 "], null));

        Assert.True(result.IsValid);
        Assert.Equal(["PC001", "PC002", "PC003"], result.Targets.Select(static target => target.Name));
        Assert.All(result.Targets, static target => Assert.Equal("computer-name", target.Source));
    }

    [Fact]
    public void ResolvesTargetFileWithCommentsBlanksAndDuplicates()
    {
        using var targetFile = TemporaryTargetFile.Create("""
            # patch batch
            PC010

              pc011
            PC010
            PC012,pc011,PC013
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput([], targetFile.Path));

        Assert.True(result.IsValid);
        Assert.Equal(["PC010", "pc011", "PC012", "PC013"], result.Targets.Select(static target => target.Name));
        Assert.Equal($"target-file:{targetFile.Path}:2", result.Targets[0].Source);
        Assert.Equal($"target-file:{targetFile.Path}:4", result.Targets[1].Source);
        Assert.Equal($"target-file:{targetFile.Path}:6", result.Targets[2].Source);
        Assert.Equal($"target-file:{targetFile.Path}:6", result.Targets[3].Source);
    }

    [Fact]
    public void CommandLineTargetsWinWhenTargetFileContainsCaseInsensitiveDuplicate()
    {
        using var targetFile = TemporaryTargetFile.Create("pc001\r\nPC002");

        var result = TargetResolver.Resolve(new TargetResolutionInput(["PC001"], targetFile.Path));

        Assert.True(result.IsValid);
        Assert.Equal(["PC001", "PC002"], result.Targets.Select(static target => target.Name));
        Assert.Equal("computer-name", result.Targets[0].Source);
        Assert.Equal($"target-file:{targetFile.Path}:2", result.Targets[1].Source);
    }

    [Fact]
    public void EmptyTargetSourcesFailClearly()
    {
        var result = TargetResolver.Resolve(new TargetResolutionInput([" , "], null));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "TargetsRequired");
    }

    [Fact]
    public void MissingTargetFileFailsClearly()
    {
        var result = TargetResolver.Resolve(new TargetResolutionInput([], @"C:\Missing\Targets.txt"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "TargetFileNotFound");
    }

    [Fact]
    public void ResolvesTextInventoryWhenNoSelectorIsProvided()
    {
        using var inventory = TemporaryTargetFile.Create("""
            # inventory
            PC100
            PC101,pc100
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput([], null, InventoryPath: inventory.Path));

        Assert.True(result.IsValid);
        Assert.Equal(["PC100", "PC101"], result.Targets.Select(static target => target.Name));
        Assert.All(result.Targets, target => Assert.StartsWith($"inventory:{inventory.Path}", target.Source));
    }

    [Fact]
    public void ResolvesInlineListTopLevelHostInventoryWhenNoSelectorIsProvided()
    {
        using var inventory = TemporaryTargetFile.Create("""
            hosts: [WEB01, WEB02]
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput([], null, InventoryPath: inventory.Path));

        Assert.True(result.IsValid);
        Assert.Equal(["WEB01", "WEB02"], result.Targets.Select(static target => target.Name));
        Assert.All(result.Targets, target => Assert.Equal($"inventory:{inventory.Path}", target.Source));
    }

    [Fact]
    public void ResolvesYamlInventoryGroupsHostsTagsAndExcludes()
    {
        using var inventory = TemporaryTargetFile.Create("""
            groups:
              web:
                hosts:
                  - WEB01
                  - WEB02
              db:
                hosts:
                  - DB01
            hosts:
              WEB01:
                tags: [prod, iis]
              WEB02:
                tags: [test, iis]
              DB01:
                tags:
                  - prod
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["web,tag:prod"],
            InventoryPath: inventory.Path,
            ExcludeSelectors: ["WEB02"]));

        Assert.True(result.IsValid);
        Assert.Equal(["WEB01", "DB01"], result.Targets.Select(static target => target.Name));
        Assert.All(result.Targets, target => Assert.Equal($"inventory:{inventory.Path}", target.Source));
    }

    [Fact]
    public void ResolvesInventoryTransportFromHostGroupVarsAndDefaults()
    {
        using var inventory = TemporaryTargetFile.Create("""
            defaults:
              transport: winrm
            groups:
              web:
                vars:
                  transport: psrp
                hosts:
                  - WEB01
                  - WEB02
            hosts:
              WEB01:
                vars:
                  transport: psexec
              WEB02:
                tags: [prod]
            """);

        var hostOverride = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["WEB01"],
            InventoryPath: inventory.Path));
        var groupOverride = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["WEB02"],
            InventoryPath: inventory.Path));

        Assert.True(hostOverride.IsValid);
        Assert.Equal(TransportKind.PsExec, hostOverride.InventoryTransport);
        Assert.True(groupOverride.IsValid);
        Assert.Equal(TransportKind.Psrp, groupOverride.InventoryTransport);
    }

    [Fact]
    public void ResolvesInventoryTransportFromInlineDefaultsGroupVarsAndHostVars()
    {
        using var inventory = TemporaryTargetFile.Create("""
            defaults: { transport: winrm }
            groups:
              web:
                vars: { transport: psrp }
                hosts: [WEB01, WEB02]
            hosts:
              WEB01:
                vars: { transport: psexec }
              APP01:
                tags: [prod]
            """);

        var hostOverride = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["WEB01"],
            InventoryPath: inventory.Path));
        var groupOverride = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["WEB02"],
            InventoryPath: inventory.Path));
        var defaultsFallback = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["APP01"],
            InventoryPath: inventory.Path));

        Assert.True(hostOverride.IsValid);
        Assert.Equal(TransportKind.PsExec, hostOverride.InventoryTransport);
        Assert.True(groupOverride.IsValid);
        Assert.Equal(TransportKind.Psrp, groupOverride.InventoryTransport);
        Assert.True(defaultsFallback.IsValid);
        Assert.Equal(TransportKind.WinRm, defaultsFallback.InventoryTransport);
    }

    [Fact]
    public void ResolvesTopLevelInlineMapHostEntriesUsingTagsAndVarsTransport()
    {
        using var inventory = TemporaryTargetFile.Create("""
            defaults: { transport: winrm }
            hosts:
              WEB01: { tags: [prod, iis], vars: { transport: psexec } }
              WEB02: { tags: [prod] }
            """);

        var taggedHost = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["tag:prod"],
            InventoryPath: inventory.Path,
            ExcludeSelectors: ["WEB02"]));
        var defaultsFallback = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["WEB02"],
            InventoryPath: inventory.Path));

        Assert.True(taggedHost.IsValid);
        Assert.Equal(["WEB01"], taggedHost.Targets.Select(static target => target.Name));
        Assert.Equal(TransportKind.PsExec, taggedHost.InventoryTransport);
        Assert.True(defaultsFallback.IsValid);
        Assert.Equal(TransportKind.WinRm, defaultsFallback.InventoryTransport);
    }

    [Fact]
    public void UnsupportedTopLevelInlineMapHostFieldFailsClearly()
    {
        using var inventory = TemporaryTargetFile.Create("""
            hosts:
              WEB01: { credential: prod-admin }
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["WEB01"],
            InventoryPath: inventory.Path));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "InventoryFieldUnsupported");
    }

    [Fact]
    public void ResolvesNestedInventoryGroupsAndInheritedTransport()
    {
        using var inventory = TemporaryTargetFile.Create("""
            defaults:
              transport: winrm
            groups:
              prod:
                vars:
                  transport: psrp
                children:
                  - web
              web:
                hosts:
                  - WEB01
                  - WEB02
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["prod"],
            InventoryPath: inventory.Path));

        Assert.True(result.IsValid);
        Assert.Equal(["WEB01", "WEB02"], result.Targets.Select(static target => target.Name));
        Assert.Equal(TransportKind.Psrp, result.InventoryTransport);
        Assert.All(result.Targets, target => Assert.Equal($"inventory:{inventory.Path}", target.Source));
    }

    [Fact]
    public void ResolvesInventoryDefaultTransportForInventoryHostWithoutOverrides()
    {
        using var inventory = TemporaryTargetFile.Create("""
            defaults:
              transport: winrm
            hosts:
              WEB01:
                tags: [prod]
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["WEB01"],
            InventoryPath: inventory.Path));

        Assert.True(result.IsValid);
        Assert.Equal(TransportKind.WinRm, result.InventoryTransport);
    }

    [Fact]
    public void DefaultsOnlyYamlInventoryDoesNotInventHostsAndRequiresRealTargets()
    {
        using var inventory = TemporaryTargetFile.Create("""
            defaults:
              transport: winrm
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            InventoryPath: inventory.Path));

        Assert.False(result.IsValid);
        Assert.Empty(result.Targets);
        Assert.Contains(result.Errors, static error => error.Code == "TargetsRequired");
    }

    [Fact]
    public void DefaultsOnlyYamlInventoryAllSelectorFailsClearly()
    {
        using var inventory = TemporaryTargetFile.Create("""
            defaults:
              transport: winrm
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["all"],
            InventoryPath: inventory.Path));

        Assert.False(result.IsValid);
        Assert.Empty(result.Targets);
        Assert.Contains(result.Errors, static error => error.Code == "TargetSelectorMatchedNoTargets");
    }

    [Fact]
    public void ConflictingInventoryTransportPoliciesFailClearly()
    {
        using var inventory = TemporaryTargetFile.Create("""
            hosts:
              WEB01:
                transport: winrm
              WEB02:
                transport: psrp
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["WEB01,WEB02"],
            InventoryPath: inventory.Path));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "InventoryTransportConflict");
    }

    [Fact]
    public void UnsupportedInventoryTransportFailsClearly()
    {
        using var inventory = TemporaryTargetFile.Create("""
            defaults:
              transport: ssh
            hosts:
              WEB01:
                tags: [prod]
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["WEB01"],
            InventoryPath: inventory.Path));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "InventoryTransportInvalid");
    }

    [Fact]
    public void UnsupportedInventorySectionFailsClearly()
    {
        using var inventory = TemporaryTargetFile.Create("""
            defaults:
              transport: winrm
            metadata:
              owner: ops
            hosts:
              WEB01:
                tags: [prod]
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["WEB01"],
            InventoryPath: inventory.Path));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "InventorySectionUnsupported");
    }

    [Fact]
    public void UnsupportedInventoryDefaultsFieldFailsClearly()
    {
        using var inventory = TemporaryTargetFile.Create("""
            defaults:
              credential: prod-admin
            hosts:
              WEB01:
                tags: [prod]
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["WEB01"],
            InventoryPath: inventory.Path));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "InventoryFieldUnsupported");
    }

    [Fact]
    public void InventoryGroupCycleFailsClearly()
    {
        using var inventory = TemporaryTargetFile.Create("""
            groups:
              web:
                children:
                  - prod
              prod:
                children:
                  - web
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["web"],
            InventoryPath: inventory.Path));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "InventoryGroupCycle");
    }

    [Fact]
    public void MappingFormGroupHostsResolveTargets()
    {
        using var inventory = TemporaryTargetFile.Create("""
            groups:
              web:
                hosts:
                  WEB01:
                  WEB02:
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["web"],
            InventoryPath: inventory.Path));

        Assert.True(result.IsValid);
        Assert.Equal(["WEB01", "WEB02"], result.Targets.Select(static target => target.Name));
    }

    [Fact]
    public void MappingFormGroupChildrenResolveTransitively()
    {
        using var inventory = TemporaryTargetFile.Create("""
            groups:
              web:
                children:
                  prod:
              prod:
                hosts:
                  WEB01:
                  WEB02:
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["web"],
            InventoryPath: inventory.Path));

        Assert.True(result.IsValid);
        Assert.Equal(["WEB01", "WEB02"], result.Targets.Select(static target => target.Name));
    }

    [Fact]
    public void InlineListFormGroupHostsResolveTargets()
    {
        using var inventory = TemporaryTargetFile.Create("""
            groups:
              web:
                hosts: [WEB01, WEB02]
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["web"],
            InventoryPath: inventory.Path));

        Assert.True(result.IsValid);
        Assert.Equal(["WEB01", "WEB02"], result.Targets.Select(static target => target.Name));
    }

    [Fact]
    public void InlineListFormGroupChildrenResolveTransitively()
    {
        using var inventory = TemporaryTargetFile.Create("""
            groups:
              web:
                children: [prod]
              prod:
                hosts: [WEB01, WEB02]
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["web"],
            InventoryPath: inventory.Path));

        Assert.True(result.IsValid);
        Assert.Equal(["WEB01", "WEB02"], result.Targets.Select(static target => target.Name));
    }

    [Fact]
    public void UnsupportedInventoryHostFieldFailsClearly()
    {
        using var inventory = TemporaryTargetFile.Create("""
            hosts:
              WEB01:
                credential: prod-admin
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["WEB01"],
            InventoryPath: inventory.Path));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "InventoryFieldUnsupported");
    }

    [Fact]
    public void UnsupportedInlineInventoryVarFieldFailsClearly()
    {
        using var inventory = TemporaryTargetFile.Create("""
            groups:
              web:
                vars: { credential: prod-admin }
                hosts: [WEB01]
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            [],
            null,
            TargetSelectors: ["web"],
            InventoryPath: inventory.Path));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "InventoryFieldUnsupported");
    }

    [Fact]
    public void SelectorFileCanBeUsedWithoutInventory()
    {
        using var targetFile = TemporaryTargetFile.Create("PC200\r\nPC201");

        var result = TargetResolver.Resolve(new TargetResolutionInput([], null, TargetSelectors: [$"file:{targetFile.Path}"]));

        Assert.True(result.IsValid);
        Assert.Equal(["PC200", "PC201"], result.Targets.Select(static target => target.Name));
    }

    [Fact]
    public void SelectorFileAllowsExpressionCharactersInPath()
    {
        using var targetFile = TemporaryTargetFile.Create("PC210", prefix: "targets&prod");

        var result = TargetResolver.Resolve(new TargetResolutionInput([], null, TargetSelectors: [$"file:{targetFile.Path}"]));

        Assert.True(result.IsValid);
        Assert.Equal(["PC210"], result.Targets.Select(static target => target.Name));
    }

    [Fact]
    public void EmptyAllSelectorFailsClearly()
    {
        var result = TargetResolver.Resolve(new TargetResolutionInput([], null, TargetSelectors: ["all"]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "TargetSelectorMatchedNoTargets");
    }

    [Fact]
    public void EmptyTagSelectorFailsClearly()
    {
        var result = TargetResolver.Resolve(new TargetResolutionInput([], null, TargetSelectors: ["tag:"]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "TargetSelectorInvalid");
    }

    [Fact]
    public void EmptyFileSelectorFailsClearly()
    {
        var result = TargetResolver.Resolve(new TargetResolutionInput([], null, TargetSelectors: ["file:"]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "TargetSelectorInvalid");
    }

    [Fact]
    public void UnsupportedAdvancedSelectorExpressionFailsClearly()
    {
        var result = TargetResolver.Resolve(new TargetResolutionInput([], null, TargetSelectors: ["web:&prod"]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "TargetSelectorUnsupported");
    }

    [Fact]
    public void UnmatchedTagSelectorFailsClearlyEvenWhenOtherTargetsResolve()
    {
        using var inventory = TemporaryTargetFile.Create("""
            hosts:
              WEB01:
                tags: [prod]
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            ["PC300"],
            null,
            TargetSelectors: ["tag:missing"],
            InventoryPath: inventory.Path));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "TargetSelectorMatchedNoTargets");
    }

    [Fact]
    public void ExcludeFileSelectorRemovesTargetsFromFile()
    {
        using var targetFile = TemporaryTargetFile.Create("PC401");

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            ["PC400,PC401"],
            null,
            ExcludeSelectors: [$"file:{targetFile.Path}"]));

        Assert.True(result.IsValid);
        Assert.Equal(["PC400"], result.Targets.Select(static target => target.Name));
    }

    [Fact]
    public void ExcludeFileSelectorAllowsExpressionCharactersInPath()
    {
        using var targetFile = TemporaryTargetFile.Create("PC411", prefix: "exclude&prod");

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            ["PC410,PC411"],
            null,
            ExcludeSelectors: [$"file:{targetFile.Path}"]));

        Assert.True(result.IsValid);
        Assert.Equal(["PC410"], result.Targets.Select(static target => target.Name));
    }

    [Fact]
    public void ExcludeAllSelectorRemovesInventoryTargets()
    {
        using var inventory = TemporaryTargetFile.Create("""
            hosts:
              WEB01:
                tags: [prod]
            """);

        var result = TargetResolver.Resolve(new TargetResolutionInput(
            ["PC500"],
            null,
            TargetSelectors: ["all"],
            InventoryPath: inventory.Path,
            ExcludeSelectors: ["all"]));

        Assert.True(result.IsValid);
        Assert.Equal(["PC500"], result.Targets.Select(static target => target.Name));
    }

    [Fact]
    public void InvalidExcludeSelectorFailsClearly()
    {
        var result = TargetResolver.Resolve(new TargetResolutionInput(["PC600"], null, ExcludeSelectors: ["tag:"]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "TargetSelectorInvalid");
    }
}
