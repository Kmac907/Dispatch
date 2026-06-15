using Dispatch.Core.Targeting;

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
    public void SelectorFileCanBeUsedWithoutInventory()
    {
        using var targetFile = TemporaryTargetFile.Create("PC200\r\nPC201");

        var result = TargetResolver.Resolve(new TargetResolutionInput([], null, TargetSelectors: [$"file:{targetFile.Path}"]));

        Assert.True(result.IsValid);
        Assert.Equal(["PC200", "PC201"], result.Targets.Select(static target => target.Name));
    }
}
