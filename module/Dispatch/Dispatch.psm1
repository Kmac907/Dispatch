Set-StrictMode -Version Latest

function Resolve-DispatchExecutable {
    [CmdletBinding()]
    param(
        [string] $DispatchPath
    )

    if (-not [string]::IsNullOrWhiteSpace($DispatchPath)) {
        if (Test-Path -LiteralPath $DispatchPath -PathType Leaf) {
            return (Resolve-Path -LiteralPath $DispatchPath).ProviderPath
        }

        throw "Dispatch executable was not found at '$DispatchPath'."
    }

    if (-not [string]::IsNullOrWhiteSpace($env:DISPATCH_EXE)) {
        if (Test-Path -LiteralPath $env:DISPATCH_EXE -PathType Leaf) {
            return (Resolve-Path -LiteralPath $env:DISPATCH_EXE).ProviderPath
        }

        throw "DISPATCH_EXE is set to '$env:DISPATCH_EXE', but that file does not exist."
    }

    $bundledPath = Join-Path -Path $PSScriptRoot -ChildPath 'bin\win-x64\dispatch.exe'
    if (Test-Path -LiteralPath $bundledPath -PathType Leaf) {
        return (Resolve-Path -LiteralPath $bundledPath).ProviderPath
    }

    foreach ($commandName in @('dispatch.exe', 'dispatch')) {
        $command = Get-Command -Name $commandName -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $command) {
            return $command.Source
        }
    }

    throw "dispatch.exe was not found. Install Dispatch, add dispatch.exe to PATH, set DISPATCH_EXE, or pass -DispatchPath."
}

function Invoke-DispatchNative {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]] $ArgumentList,

        [string] $DispatchPath
    )

    $resolvedPath = Resolve-DispatchExecutable -DispatchPath $DispatchPath
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $resolvedPath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = ($ArgumentList | ForEach-Object { ConvertTo-DispatchNativeArgument -Argument $_ }) -join ' '

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo

    try {
        if (-not $process.Start()) {
            throw "Failed to start dispatch.exe."
        }

        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()

        [pscustomobject]@{
            DispatchPath = $resolvedPath
            ExitCode = $process.ExitCode
            StdOut = $stdout
            StdErr = $stderr
        }
    }
    finally {
        $process.Dispose()
    }
}

function ConvertTo-DispatchNativeArgument {
    [CmdletBinding()]
    param(
        [AllowEmptyString()]
        [string] $Argument
    )

    if ($null -eq $Argument -or $Argument.Length -eq 0) {
        return '""'
    }

    if ($Argument -notmatch '[\s"]') {
        return $Argument
    }

    $builder = [System.Text.StringBuilder]::new()
    [void] $builder.Append('"')
    $backslashes = 0

    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            if ($backslashes -gt 0) {
                [void] $builder.Append('\' * ($backslashes * 2))
                $backslashes = 0
            }

            [void] $builder.Append('\"')
            continue
        }

        if ($backslashes -gt 0) {
            [void] $builder.Append('\' * $backslashes)
            $backslashes = 0
        }

        [void] $builder.Append($character)
    }

    if ($backslashes -gt 0) {
        [void] $builder.Append('\' * ($backslashes * 2))
    }

    [void] $builder.Append('"')
    $builder.ToString()
}

function Get-DispatchVersion {
    [CmdletBinding()]
    param(
        [string] $DispatchPath
    )

    $result = Invoke-DispatchNative -ArgumentList @('version') -DispatchPath $DispatchPath
    if ($result.ExitCode -ne 0) {
        $message = if ([string]::IsNullOrWhiteSpace($result.StdErr)) { $result.StdOut } else { $result.StdErr }
        throw "dispatch.exe version failed with exit code $($result.ExitCode). $message"
    }

    $lines = $result.StdOut -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $version = $null
    $commandService = $null

    foreach ($line in $lines) {
        if ($line -match '^Version:\s*(.+)$') {
            $version = $Matches[1]
        }
        elseif ($line -match '^Command service:\s*(.+)$') {
            $commandService = $Matches[1]
        }
    }

    [pscustomobject]@{
        Product = if ($lines.Count -gt 0) { $lines[0] } else { 'Dispatch' }
        Version = $version
        CommandService = $commandService
        DispatchPath = $result.DispatchPath
        RawOutput = $result.StdOut
    }
}

function Test-Dispatch {
    [CmdletBinding()]
    param(
        [ValidateSet('auto', 'psexec', 'psrp', 'winrm')]
        [string] $Transport = 'auto',

        [string] $DispatchPath,

        [switch] $Raw
    )

    $result = Invoke-DispatchNative -ArgumentList @('doctor', '--transport', $Transport, '--output', 'json') -DispatchPath $DispatchPath
    if ($Raw) {
        return $result
    }

    try {
        $report = ConvertFrom-DispatchJson -Json $result.StdOut
    }
    catch {
        $message = if ([string]::IsNullOrWhiteSpace($result.StdErr)) { $result.StdOut } else { $result.StdErr }
        throw "dispatch.exe doctor did not return valid JSON. Exit code: $($result.ExitCode). $message"
    }

    $report | Add-Member -NotePropertyName ExitCode -NotePropertyValue $result.ExitCode -Force
    $report | Add-Member -NotePropertyName DispatchPath -NotePropertyValue $result.DispatchPath -Force
    $report
}

function Invoke-DispatchPowerShell {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Script,

        [string] $Target,

        [string] $Inventory,

        [string] $Config,

        [string] $Exclude,

        [string] $CredentialName,

        [ValidateSet('auto', 'psexec', 'psrp', 'winrm')]
        [string] $Transport,

        [int[]] $ExpectedExitCode,

        [int] $Throttle,

        [string[]] $ArtifactPath,

        [string] $OutputRoot,

        [string] $RemoteRoot,

        [string] $TargetFile,

        [string[]] $Secret,

        [string[]] $ArgumentList,

        [switch] $Plan,

        [switch] $RunAsSystem,

        [switch] $NoColor,

        [switch] $Quiet,

        [switch] $Trace,

        [string] $DispatchPath,

        [switch] $Raw
    )

    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.Add('run')
    $arguments.Add('ps')
    $arguments.Add($Script)
    Add-DispatchRunCommonArguments -Arguments $arguments -Target $Target -Inventory $Inventory -Config $Config -Exclude $Exclude -CredentialName $CredentialName -Transport $Transport -ExpectedExitCode $ExpectedExitCode -Throttle $Throttle -ArtifactPath $ArtifactPath -OutputRoot $OutputRoot -RemoteRoot $RemoteRoot -TargetFile $TargetFile -Plan:$Plan -RunAsSystem:$RunAsSystem -NoColor:$NoColor -Quiet:$Quiet -VerboseEnabled:($PSBoundParameters.ContainsKey('Verbose') -and $PSBoundParameters['Verbose']) -Trace:$Trace
    Add-DispatchRepeatedArgumentValue -Arguments $arguments -Name '--secret' -Value $Secret
    Add-DispatchStructuredOutputArguments -Arguments $arguments
    Add-DispatchRemainingArguments -Arguments $arguments -Value $ArgumentList

    Invoke-DispatchStructuredRun -ArgumentList $arguments.ToArray() -DispatchPath $DispatchPath -Raw:$Raw
}

function Invoke-DispatchCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Command,

        [string] $Target,

        [string] $Inventory,

        [string] $Config,

        [string] $Exclude,

        [string] $CredentialName,

        [ValidateSet('auto', 'psrp', 'winrm')]
        [string] $Transport,

        [int[]] $ExpectedExitCode,

        [int] $Throttle,

        [string[]] $ArtifactPath,

        [string] $OutputRoot,

        [string] $RemoteRoot,

        [string] $TargetFile,

        [string[]] $ArgumentList,

        [switch] $Plan,

        [switch] $RunAsSystem,

        [switch] $NoColor,

        [switch] $Quiet,

        [switch] $Trace,

        [string] $DispatchPath,

        [switch] $Raw
    )

    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.Add('run')
    $arguments.Add('cmd')
    $arguments.Add($Command)
    Add-DispatchRunCommonArguments -Arguments $arguments -Target $Target -Inventory $Inventory -Config $Config -Exclude $Exclude -CredentialName $CredentialName -Transport $Transport -ExpectedExitCode $ExpectedExitCode -Throttle $Throttle -ArtifactPath $ArtifactPath -OutputRoot $OutputRoot -RemoteRoot $RemoteRoot -TargetFile $TargetFile -Plan:$Plan -RunAsSystem:$RunAsSystem -NoColor:$NoColor -Quiet:$Quiet -VerboseEnabled:($PSBoundParameters.ContainsKey('Verbose') -and $PSBoundParameters['Verbose']) -Trace:$Trace
    Add-DispatchStructuredOutputArguments -Arguments $arguments
    Add-DispatchRemainingArguments -Arguments $arguments -Value $ArgumentList

    Invoke-DispatchStructuredRun -ArgumentList $arguments.ToArray() -DispatchPath $DispatchPath -Raw:$Raw
}

function Invoke-DispatchExecutable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [string] $Target,

        [string] $Inventory,

        [string] $Config,

        [string] $Exclude,

        [string] $CredentialName,

        [ValidateSet('auto', 'psrp', 'winrm')]
        [string] $Transport,

        [int[]] $ExpectedExitCode,

        [int] $Throttle,

        [string[]] $ArtifactPath,

        [string] $OutputRoot,

        [string] $RemoteRoot,

        [string] $TargetFile,

        [string[]] $ArgumentList,

        [switch] $Plan,

        [switch] $RunAsSystem,

        [switch] $NoColor,

        [switch] $Quiet,

        [switch] $Trace,

        [string] $DispatchPath,

        [switch] $Raw
    )

    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.Add('run')
    $arguments.Add('exe')
    $arguments.Add($Path)
    Add-DispatchRunCommonArguments -Arguments $arguments -Target $Target -Inventory $Inventory -Config $Config -Exclude $Exclude -CredentialName $CredentialName -Transport $Transport -ExpectedExitCode $ExpectedExitCode -Throttle $Throttle -ArtifactPath $ArtifactPath -OutputRoot $OutputRoot -RemoteRoot $RemoteRoot -TargetFile $TargetFile -Plan:$Plan -RunAsSystem:$RunAsSystem -NoColor:$NoColor -Quiet:$Quiet -VerboseEnabled:($PSBoundParameters.ContainsKey('Verbose') -and $PSBoundParameters['Verbose']) -Trace:$Trace
    Add-DispatchStructuredOutputArguments -Arguments $arguments
    Add-DispatchRemainingArguments -Arguments $arguments -Value $ArgumentList

    Invoke-DispatchStructuredRun -ArgumentList $arguments.ToArray() -DispatchPath $DispatchPath -Raw:$Raw
}

function Invoke-DispatchStructuredRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]] $ArgumentList,

        [string] $DispatchPath,

        [switch] $Raw
    )

    $result = Invoke-DispatchNative -ArgumentList $ArgumentList -DispatchPath $DispatchPath
    if ($Raw) {
        return $result
    }

    try {
        $runResult = ConvertFrom-DispatchJson -Json $result.StdOut
    }
    catch {
        $message = if ([string]::IsNullOrWhiteSpace($result.StdErr)) { $result.StdOut } else { $result.StdErr }
        throw "dispatch.exe run did not return valid JSON. Exit code: $($result.ExitCode). $message"
    }

    $runResult | Add-Member -NotePropertyName ExitCode -NotePropertyValue $result.ExitCode -Force
    $runResult | Add-Member -NotePropertyName DispatchPath -NotePropertyValue $result.DispatchPath -Force
    $runResult
}

function Add-DispatchStructuredOutputArguments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Collections.Generic.List[string]] $Arguments
    )

    $Arguments.Add('--output')
    $Arguments.Add('json')
    $Arguments.Add('--no-progress')
}

function Add-DispatchRunCommonArguments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Collections.Generic.List[string]] $Arguments,

        [string] $Target,

        [string] $Inventory,

        [string] $Config,

        [string] $Exclude,

        [string] $CredentialName,

        [string] $Transport,

        [int[]] $ExpectedExitCode,

        [int] $Throttle,

        [string[]] $ArtifactPath,

        [string] $OutputRoot,

        [string] $RemoteRoot,

        [string] $TargetFile,

        [switch] $Plan,

        [switch] $RunAsSystem,

        [switch] $NoColor,

        [switch] $Quiet,

        [bool] $VerboseEnabled,

        [switch] $Trace
    )

    Add-DispatchArgumentValue -Arguments $Arguments -Name '--target' -Value $Target
    Add-DispatchArgumentValue -Arguments $Arguments -Name '--inventory' -Value $Inventory
    Add-DispatchArgumentValue -Arguments $Arguments -Name '--config' -Value $Config
    Add-DispatchArgumentValue -Arguments $Arguments -Name '--exclude' -Value $Exclude
    Add-DispatchArgumentValue -Arguments $Arguments -Name '--credential' -Value $CredentialName
    Add-DispatchArgumentValue -Arguments $Arguments -Name '--transport' -Value $Transport
    if ($ExpectedExitCode -and $ExpectedExitCode.Count -gt 0) {
        Add-DispatchArgumentValue -Arguments $Arguments -Name '--expected-exit-code' -Value ($ExpectedExitCode -join ',')
    }

    if ($Throttle -gt 0) {
        Add-DispatchArgumentValue -Arguments $Arguments -Name '--throttle' -Value ([string] $Throttle)
    }

    Add-DispatchRepeatedArgumentValue -Arguments $Arguments -Name '--artifact-path' -Value $ArtifactPath
    Add-DispatchArgumentValue -Arguments $Arguments -Name '--output-root' -Value $OutputRoot
    Add-DispatchArgumentValue -Arguments $Arguments -Name '--remote-root' -Value $RemoteRoot
    Add-DispatchArgumentValue -Arguments $Arguments -Name '--target-file' -Value $TargetFile

    if ($Plan) {
        $Arguments.Add('--plan')
    }

    if ($RunAsSystem) {
        $Arguments.Add('--run-as-system')
    }

    if ($NoColor) {
        $Arguments.Add('--no-color')
    }

    if ($Quiet) {
        $Arguments.Add('--quiet')
    }

    if ($VerboseEnabled) {
        $Arguments.Add('--verbose')
    }

    if ($Trace) {
        $Arguments.Add('--trace')
    }
}

function Add-DispatchArgumentValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Collections.Generic.List[string]] $Arguments,

        [Parameter(Mandatory)]
        [string] $Name,

        [AllowEmptyString()]
        [string] $Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }

    $Arguments.Add($Name)
    $Arguments.Add($Value)
}

function Add-DispatchRepeatedArgumentValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Collections.Generic.List[string]] $Arguments,

        [Parameter(Mandatory)]
        [string] $Name,

        [string[]] $Value
    )

    foreach ($item in $Value) {
        Add-DispatchArgumentValue -Arguments $Arguments -Name $Name -Value $item
    }
}

function Add-DispatchRemainingArguments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Collections.Generic.List[string]] $Arguments,

        [string[]] $Value
    )

    if ($null -eq $Value -or $Value.Count -eq 0) {
        return
    }

    $Arguments.Add('--')
    foreach ($item in $Value) {
        $Arguments.Add($item)
    }
}

function ConvertFrom-DispatchJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Json
    )

    $command = Get-Command -Name ConvertFrom-Json -CommandType Cmdlet
    if ($command.Parameters.ContainsKey('Depth')) {
        return $Json | ConvertFrom-Json -Depth 64
    }

    $Json | ConvertFrom-Json
}

Export-ModuleMember -Function Get-DispatchVersion, Invoke-DispatchCommand, Invoke-DispatchExecutable, Invoke-DispatchPowerShell, Test-Dispatch
