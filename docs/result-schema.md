# Result Schema

`Admin\results.json` is the durable final result contract. `Admin\events.ndjson` is the durable event stream.

## Run Fields

Minimum run fields:

```text
runId
startedAt
endedAt
durationMs
requestedBy
transport
payloadType
payloadName
targetCount
successCount
failedCount
cancelledCount
timedOutCount
resultPath
targets
```

## Target Fields

Minimum per-target fields:

```text
runId
target
transport
payloadType
payloadName
state
exitCode
expectedExitCodes
startedAt
endedAt
durationMs
failureCategory
failureMessage
stdoutPath
stderrPath
resultPath
artifacts
artifactCollectionStatus
artifactCollectionFailureMessage
secretHandoffStatus
cleanupStatus
transportMetadata
streamRecords
```

`streamRecords` is optional and mainly used by PSRP.

## Artifact References

Artifacts include copied-back script-created files and metadata describing collection status. Missing default folders should produce `not-found`, not execution failure.

## Stability

Automation should:

- read `Admin\results.json` for final state
- read `Admin\events.ndjson` for event history
- avoid parsing rich console output
- tolerate additive fields
- treat enum values as stable contracts
