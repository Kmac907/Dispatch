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

        [string] $DispatchPath,

        [string] $CredentialHandoffPath
    )

    $resolvedPath = Resolve-DispatchExecutable -DispatchPath $DispatchPath
    $nativeArguments = ($ArgumentList | ForEach-Object { ConvertTo-DispatchNativeArgument -Argument $_ }) -join ' '
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    if (-not [string]::IsNullOrWhiteSpace($CredentialHandoffPath)) {
        $startInfo.Environment['DISPATCH_PSCREDENTIAL_HANDOFF'] = $CredentialHandoffPath
    }

    if ($resolvedPath -match '\.(bat|cmd)$') {
        $commandProcessor = $env:ComSpec
        if ([string]::IsNullOrWhiteSpace($commandProcessor) -or -not (Test-Path -LiteralPath $commandProcessor -PathType Leaf)) {
            $commandProcessor = Join-Path -Path $env:SystemRoot -ChildPath 'System32\cmd.exe'
        }

        $startInfo.FileName = $commandProcessor
        $nativeCommand = ConvertTo-DispatchNativeArgument -Argument $resolvedPath
        if ([string]::IsNullOrWhiteSpace($nativeArguments)) {
            $startInfo.Arguments = "/d /s /c $nativeCommand"
        }
        else {
            $startInfo.Arguments = "/d /s /c ""$nativeCommand $nativeArguments"""
        }
    }
    else {
        $startInfo.FileName = $resolvedPath
        $startInfo.Arguments = $nativeArguments
    }

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

        [System.Management.Automation.PSCredential] $Credential,

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

    $handoffPath = New-DispatchPSCredentialHandoff -CredentialName $CredentialName -Credential $Credential -Config $Config
    try {
        Invoke-DispatchStructuredRun -ArgumentList $arguments.ToArray() -DispatchPath $DispatchPath -CredentialHandoffPath $handoffPath -Raw:$Raw
    }
    finally {
        Remove-DispatchPSCredentialHandoff -Path $handoffPath
    }
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

        [System.Management.Automation.PSCredential] $Credential,

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

    $handoffPath = New-DispatchPSCredentialHandoff -CredentialName $CredentialName -Credential $Credential -Config $Config
    try {
        Invoke-DispatchStructuredRun -ArgumentList $arguments.ToArray() -DispatchPath $DispatchPath -CredentialHandoffPath $handoffPath -Raw:$Raw
    }
    finally {
        Remove-DispatchPSCredentialHandoff -Path $handoffPath
    }
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

        [System.Management.Automation.PSCredential] $Credential,

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

    $handoffPath = New-DispatchPSCredentialHandoff -CredentialName $CredentialName -Credential $Credential -Config $Config
    try {
        Invoke-DispatchStructuredRun -ArgumentList $arguments.ToArray() -DispatchPath $DispatchPath -CredentialHandoffPath $handoffPath -Raw:$Raw
    }
    finally {
        Remove-DispatchPSCredentialHandoff -Path $handoffPath
    }
}

function Invoke-DispatchJob {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [Alias('Job')]
        [string] $JobPath,

        [string] $Target,

        [string] $Inventory,

        [string] $Config,

        [string] $Exclude,

        [string] $CredentialName,

        [System.Management.Automation.PSCredential] $Credential,

        [ValidateSet('auto', 'psexec', 'psrp', 'winrm')]
        [string] $Transport,

        [string] $Tags,

        [string] $SkipTags,

        [int] $Serial,

        [switch] $Plan,

        [switch] $Check,

        [switch] $Diff,

        [switch] $NoColor,

        [switch] $Quiet,

        [switch] $Trace,

        [string] $DispatchPath,

        [switch] $Raw
    )

    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.Add('apply')
    $arguments.Add($JobPath)
    Add-DispatchApplyCommonArguments -Arguments $arguments -Target $Target -Inventory $Inventory -Config $Config -Exclude $Exclude -CredentialName $CredentialName -Transport $Transport -Tags $Tags -SkipTags $SkipTags -Serial $Serial -Plan:$Plan -Check:$Check -Diff:$Diff -NoColor:$NoColor -Quiet:$Quiet -VerboseEnabled:($PSBoundParameters.ContainsKey('Verbose') -and $PSBoundParameters['Verbose']) -Trace:$Trace
    Add-DispatchStructuredOutputArguments -Arguments $arguments

    $handoffPath = New-DispatchPSCredentialHandoff -CredentialName $CredentialName -Credential $Credential -Config $Config
    try {
        Invoke-DispatchStructuredRun -ArgumentList $arguments.ToArray() -DispatchPath $DispatchPath -CredentialHandoffPath $handoffPath -Raw:$Raw
    }
    finally {
        Remove-DispatchPSCredentialHandoff -Path $handoffPath
    }
}

function Invoke-DispatchStructuredRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]] $ArgumentList,

        [string] $DispatchPath,

        [string] $CredentialHandoffPath,

        [switch] $Raw
    )

    $result = Invoke-DispatchNative -ArgumentList $ArgumentList -DispatchPath $DispatchPath -CredentialHandoffPath $CredentialHandoffPath
    if ($Raw) {
        return $result
    }

    try {
        $runResult = ConvertFrom-DispatchJson -Json $result.StdOut
    }
    catch {
        $message = if ([string]::IsNullOrWhiteSpace($result.StdErr)) { $result.StdOut } else { $result.StdErr }
        throw "dispatch.exe did not return valid JSON. Exit code: $($result.ExitCode). $message"
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

function Add-DispatchApplyCommonArguments {
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

        [string] $Tags,

        [string] $SkipTags,

        [int] $Serial,

        [switch] $Plan,

        [switch] $Check,

        [switch] $Diff,

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
    Add-DispatchArgumentValue -Arguments $Arguments -Name '--tags' -Value $Tags
    Add-DispatchArgumentValue -Arguments $Arguments -Name '--skip-tags' -Value $SkipTags

    if ($Serial -gt 0) {
        Add-DispatchArgumentValue -Arguments $Arguments -Name '--serial' -Value ([string] $Serial)
    }

    if ($Plan) {
        $Arguments.Add('--plan')
    }

    if ($Check) {
        $Arguments.Add('--check')
    }

    if ($Diff) {
        $Arguments.Add('--diff')
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

function New-DispatchPSCredentialHandoff {
    [CmdletBinding()]
    param(
        [string] $CredentialName,

        [System.Management.Automation.PSCredential] $Credential,

        [string] $Config
    )

    if ($null -ne $Credential -and [string]::IsNullOrWhiteSpace($CredentialName)) {
        throw "-Credential requires -CredentialName so Dispatch can bind the protected handoff to a configured credential reference."
    }

    if ([string]::IsNullOrWhiteSpace($CredentialName)) {
        return $null
    }

    $definition = Get-DispatchCredentialReferenceDefinition -CredentialName $CredentialName -Config $Config
    if ($null -eq $definition -or [string]::IsNullOrWhiteSpace($definition.Provider)) {
        if ($null -ne $Credential) {
            throw "-Credential can only be used when -CredentialName resolves to provider: pscredential in Dispatch config."
        }

        return $null
    }

    if ($definition.Provider -ine 'pscredential') {
        if ($null -ne $Credential) {
            throw "-Credential can only be used with provider: pscredential. Credential '$CredentialName' uses provider: $($definition.Provider)."
        }

        return $null
    }

    if ($null -eq $Credential) {
        if ([string]::IsNullOrWhiteSpace($definition.UserName)) {
            $Credential = Get-Credential -Message "Dispatch credential '$CredentialName'"
        }
        else {
            $Credential = Get-Credential -UserName $definition.UserName -Message "Dispatch credential '$CredentialName'"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($definition.UserName) -and $Credential.UserName -ine $definition.UserName) {
        throw "Credential '$CredentialName' username '$($Credential.UserName)' does not match Dispatch config username '$($definition.UserName)'."
    }

    New-DispatchProtectedHandoffFile -CredentialName $CredentialName -Credential $Credential
}

function Get-DispatchCredentialReferenceDefinition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $CredentialName,

        [string] $Config
    )

    $configPath = Resolve-DispatchConfigPath -Config $Config
    if ([string]::IsNullOrWhiteSpace($configPath) -or -not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        return $null
    }

    $lines = Get-Content -LiteralPath $configPath
    $defaultProvider = Get-DispatchDefaultCredentialProvider -Lines $lines
    $credentialsIndent = -1
    $credentialIndent = -1
    $insideCredentials = $false
    $insideCredential = $false
    $provider = $null
    $userName = $null
    $escapedName = [regex]::Escape($CredentialName)

    foreach ($line in $lines) {
        if ($line -match '^\s*(#.*)?$') {
            continue
        }

        $indent = ($line.Length - $line.TrimStart().Length)
        if (-not $insideCredentials) {
            if ($line -match '^(\s*)credentials\s*:\s*(#.*)?$') {
                $credentialsIndent = $Matches[1].Length
                $insideCredentials = $true
            }

            continue
        }

        if ($indent -le $credentialsIndent) {
            break
        }

        if (-not $insideCredential) {
            if ($line -match "^\s{$($credentialsIndent + 2),}$escapedName\s*:\s*(#.*)?$") {
                $credentialIndent = $indent
                $insideCredential = $true
            }

            continue
        }

        if ($indent -le $credentialIndent) {
            break
        }

        if ($line -match '^\s*provider\s*:\s*(.+?)\s*(#.*)?$') {
            $provider = ConvertFrom-DispatchYamlScalar -Value $Matches[1]
        }
        elseif ($line -match '^\s*username\s*:\s*(.+?)\s*(#.*)?$') {
            $userName = ConvertFrom-DispatchYamlScalar -Value $Matches[1]
        }
    }

    if (-not $insideCredential) {
        return $null
    }

    [pscustomobject]@{
        Provider = if ([string]::IsNullOrWhiteSpace($provider)) { $defaultProvider } else { $provider }
        UserName = $userName
    }
}

function Get-DispatchDefaultCredentialProvider {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]] $Lines
    )

    $insideDispatch = $false
    $dispatchIndent = -1

    foreach ($line in $Lines) {
        if ($line -match '^\s*(#.*)?$') {
            continue
        }

        $indent = ($line.Length - $line.TrimStart().Length)
        if ($line -match '^\s*default_credential_provider\s*:\s*(.+?)\s*(#.*)?$') {
            return ConvertFrom-DispatchYamlScalar -Value $Matches[1]
        }

        if (-not $insideDispatch) {
            if ($line -match '^(\s*)dispatch\s*:\s*(#.*)?$') {
                $dispatchIndent = $Matches[1].Length
                $insideDispatch = $true
            }

            continue
        }

        if ($indent -le $dispatchIndent) {
            $insideDispatch = $false
            $dispatchIndent = -1
            continue
        }

        if ($line -match '^\s*default_credential_provider\s*:\s*(.+?)\s*(#.*)?$') {
            return ConvertFrom-DispatchYamlScalar -Value $Matches[1]
        }
    }

    $null
}

function Resolve-DispatchConfigPath {
    [CmdletBinding()]
    param(
        [string] $Config
    )

    if (-not [string]::IsNullOrWhiteSpace($Config)) {
        return $Config
    }

    $programData = if ([string]::IsNullOrWhiteSpace($env:ProgramData)) { 'C:\ProgramData' } else { $env:ProgramData }
    Join-Path -Path $programData -ChildPath 'Dispatch\config.yml'
}

function ConvertFrom-DispatchYamlScalar {
    [CmdletBinding()]
    param(
        [AllowEmptyString()]
        [string] $Value
    )

    $trimmed = if ($null -eq $Value) { '' } else { $Value.Trim() }
    if ($trimmed.Length -ge 2) {
        if (($trimmed.StartsWith("'") -and $trimmed.EndsWith("'")) -or
            ($trimmed.StartsWith('"') -and $trimmed.EndsWith('"'))) {
            return $trimmed.Substring(1, $trimmed.Length - 2)
        }
    }

    $trimmed
}

function New-DispatchProtectedHandoffFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $CredentialName,

        [Parameter(Mandatory)]
        [System.Management.Automation.PSCredential] $Credential
    )

    if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
        throw "PowerShell PSCredential handoff is supported on Windows only."
    }

    Initialize-DispatchProtectedData

    $plainTextBytes = ConvertFrom-DispatchSecureStringToUtf8Bytes -SecureString $Credential.Password
    try {
        $protectedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
            $plainTextBytes,
            $null,
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        try {
            $file = [pscustomobject]@{
                version = 1
                provider = 'pscredential'
                referenceName = $CredentialName
                userName = $Credential.UserName
                protection = 'dpapi_current_user'
                protectedValue = [Convert]::ToBase64String($protectedBytes)
                createdAt = ([DateTimeOffset]::UtcNow.ToString('O'))
            }

            $directory = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath 'Dispatch'
            [System.IO.Directory]::CreateDirectory($directory) | Out-Null
            $path = Join-Path -Path $directory -ChildPath ("pscredential-{0}.handoff" -f ([Guid]::NewGuid().ToString('N')))
            $json = $file | ConvertTo-Json -Depth 4 -Compress
            Set-Content -LiteralPath $path -Value $json -Encoding UTF8 -NoNewline
            Protect-DispatchHandoffFileAcl -Path $path
            return $path
        }
        finally {
            [Array]::Clear($protectedBytes, 0, $protectedBytes.Length)
        }
    }
    finally {
        [Array]::Clear($plainTextBytes, 0, $plainTextBytes.Length)
    }
}

function Initialize-DispatchProtectedData {
    [CmdletBinding()]
    param()

    if ('System.Security.Cryptography.ProtectedData' -as [type]) {
        return
    }

    foreach ($assemblyName in @('System.Security.Cryptography.ProtectedData', 'System.Security')) {
        try {
            Add-Type -AssemblyName $assemblyName -ErrorAction Stop
            if ('System.Security.Cryptography.ProtectedData' -as [type]) {
                return
            }
        }
        catch {
        }
    }

    throw "Unable to load DPAPI support for PowerShell PSCredential handoff."
}

function ConvertFrom-DispatchSecureStringToUtf8Bytes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [Security.SecureString] $SecureString
    )

    $pointer = [IntPtr]::Zero
    try {
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
        $plainText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        if ($null -eq $plainText) {
            return [byte[]]::new(0)
        }

        [Text.Encoding]::UTF8.GetBytes($plainText)
    }
    finally {
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
    }
}

function Protect-DispatchHandoffFileAcl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    try {
        $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        $administrators = [System.Security.Principal.SecurityIdentifier]::new([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
        $localSystem = [System.Security.Principal.SecurityIdentifier]::new([System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
        $fileInfo = [System.IO.FileInfo]::new($Path)
        $security = Get-DispatchFileSecurity -FileInfo $fileInfo
        $security.SetOwner($currentUser)
        $security.SetAccessRuleProtection($true, $false)
        $rights = [System.Security.AccessControl.FileSystemRights]::FullControl
        $allow = [System.Security.AccessControl.AccessControlType]::Allow
        $security.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($currentUser, $rights, $allow))
        $security.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($administrators, $rights, $allow))
        $security.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($localSystem, $rights, $allow))
        Set-DispatchFileSecurity -FileInfo $fileInfo -Security $security
    }
    catch {
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        throw
    }
}

function Get-DispatchFileSecurity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo] $FileInfo
    )

    if ('System.IO.FileSystemAclExtensions' -as [type]) {
        return [System.IO.FileSystemAclExtensions]::GetAccessControl($FileInfo)
    }

    foreach ($assemblyName in @('System.IO.FileSystem.AccessControl', 'System.Security.AccessControl')) {
        try {
            Add-Type -AssemblyName $assemblyName -ErrorAction Stop
            if ('System.IO.FileSystemAclExtensions' -as [type]) {
                return [System.IO.FileSystemAclExtensions]::GetAccessControl($FileInfo)
            }
        }
        catch {
        }
    }

    return $FileInfo.GetAccessControl()
}

function Set-DispatchFileSecurity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo] $FileInfo,

        [Parameter(Mandatory)]
        [System.Security.AccessControl.FileSecurity] $Security
    )

    if ('System.IO.FileSystemAclExtensions' -as [type]) {
        [System.IO.FileSystemAclExtensions]::SetAccessControl($FileInfo, $Security)
        return
    }

    $FileInfo.SetAccessControl($Security)
}

function Remove-DispatchPSCredentialHandoff {
    [CmdletBinding()]
    param(
        [string] $Path
    )

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    }
}

Export-ModuleMember -Function Get-DispatchVersion, Invoke-DispatchCommand, Invoke-DispatchExecutable, Invoke-DispatchJob, Invoke-DispatchPowerShell, Test-Dispatch
