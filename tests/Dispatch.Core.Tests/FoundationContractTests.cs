using System.Text.Json;
using Dispatch.Core;
using Dispatch.Core.Models;
using Dispatch.Transports.PsExec;
using Dispatch.Transports.WinRm;

namespace Dispatch.Core.Tests;

public sealed class FoundationContractTests
{
    [Fact]
    public void DefaultRequestSerializesWithStableEnumStrings()
    {
        var request = new DispatchRequest(
            payload: new ScriptPayload("C:\\Scripts\\Fix.ps1", ["-Verbose"]),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec);

        var json = DispatchJson.Serialize(request);

        Assert.Contains("\"transport\": \"psexec\"", json, StringComparison.Ordinal);
        Assert.Contains("\"payloadType\": \"script\"", json, StringComparison.Ordinal);
        Assert.Contains("\"expectedExitCodes\":", json, StringComparison.Ordinal);
        Assert.Contains("0", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RunResultRoundTripsThroughJson()
    {
        var result = new DispatchRunResult(
            RunId: "run-001",
            StartedAt: DateTimeOffset.Parse("2026-06-13T20:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-06-13T20:00:01Z"),
            RequestedBy: "SCF\\Admin",
            Transport: TransportKind.PsExec,
            PayloadType: PayloadKind.Script,
            PayloadName: "Fix.ps1",
            Targets:
            [
                new TargetExecutionResult(
                    RunId: "run-001",
                    Target: "PC001",
                    Transport: TransportKind.PsExec,
                    PayloadType: PayloadKind.Script,
                    PayloadName: "Fix.ps1",
                    State: TargetExecutionState.Succeeded,
                    ExitCode: 0,
                    ExpectedExitCodes: [0],
                    StartedAt: DateTimeOffset.Parse("2026-06-13T20:00:00Z"),
                    EndedAt: DateTimeOffset.Parse("2026-06-13T20:00:01Z"),
                    FailureCategory: FailureCategory.None,
                    FailureMessage: null,
                    ResultPath: "C:\\ProgramData\\Dispatch\\Runs\\run-001\\Targets\\PC001\\result.json")
            ],
            ResultPath: "C:\\ProgramData\\Dispatch\\Runs\\run-001\\Admin\\results.json");

        var json = DispatchJson.Serialize(result);
        var roundTripped = JsonSerializer.Deserialize<DispatchRunResult>(json, DispatchJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Equal("run-001", roundTripped.RunId);
        Assert.Equal("SCF\\Admin", roundTripped.RequestedBy);
        Assert.Equal(1, roundTripped.TargetCount);
        Assert.Equal(1, roundTripped.SuccessCount);
        Assert.Equal(TargetExecutionState.Succeeded, roundTripped.Targets[0].State);
    }

    [Fact]
    public void PsExecDescriptorAdvertisesV1Capabilities()
    {
        var descriptor = new PsExecTransportDescriptor();

        Assert.Equal(TransportKind.PsExec, descriptor.Kind);
        Assert.True(descriptor.Capabilities.SupportsScriptExecution);
        Assert.True(descriptor.Capabilities.RequiresEndpointLocalScriptPath);
        Assert.True(descriptor.Capabilities.SupportsRunAsSystem);
        Assert.False(descriptor.Capabilities.SupportsExplicitCredential);
        Assert.False(descriptor.Capabilities.SupportsCredentialDelegation);
    }

    [Fact]
    public void WinRmDescriptorAdvertisesCurrentSliceCapabilities()
    {
        var descriptor = new WinRmTransportDescriptor();

        Assert.Equal(TransportKind.WinRm, descriptor.Kind);
        Assert.False(descriptor.Capabilities.SupportsScriptExecution);
        Assert.True(descriptor.Capabilities.RequiresEndpointLocalScriptPath);
        Assert.False(descriptor.Capabilities.SupportsCommandExecution);
        Assert.False(descriptor.Capabilities.SupportsNativeFileCopy);
        Assert.False(descriptor.Capabilities.SupportsStreamedFileTransfer);
        Assert.False(descriptor.Capabilities.SupportsRunAsSystem);
        Assert.False(descriptor.Capabilities.SupportsExplicitCredential);
    }
}
