$ErrorActionPreference = 'Stop'

$repoRoot = 'C:\Code\Work\Endpoint-Engineering\Dispatch'
$resultPath = Join-Path $repoRoot '.codex\elevated-test-results.json'

$results = [ordered]@{
    startedAt = (Get-Date).ToString('o')
    pwd = (Get-Location).Path
    isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    commands = @()
}

Set-Location $repoRoot

$commands = @(
    [ordered]@{
        display = 'dotnet test .\tests\Dispatch.Core.Tests\Dispatch.Core.Tests.csproj --filter "FullyQualifiedName~PsrpExecutionTests|FullyQualifiedName~JobResultModelTests|FullyQualifiedName~FoundationContractTests"'
        args = @(
            'test',
            '.\tests\Dispatch.Core.Tests\Dispatch.Core.Tests.csproj',
            '--filter',
            'FullyQualifiedName~PsrpExecutionTests|FullyQualifiedName~JobResultModelTests|FullyQualifiedName~FoundationContractTests'
        )
    },
    [ordered]@{
        display = 'dotnet test .\Dispatch.sln'
        args = @(
            'test',
            '.\Dispatch.sln'
        )
    }
)

foreach ($command in $commands) {
    $output = $null
    $exitCode = 0
    try {
        $output = & dotnet @($command.args) 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    catch {
        $output = ($_ | Out-String)
        $exitCode = 1
    }

    $results.commands += [ordered]@{
        command = $command.display
        exitCode = $exitCode
        output = $output
    }

    if ($exitCode -ne 0) {
        break
    }
}

$results.endedAt = (Get-Date).ToString('o')
$results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding UTF8
