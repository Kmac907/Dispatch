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

Export-ModuleMember -Function Get-DispatchVersion, Test-Dispatch
