[CmdletBinding()]
param(
    [ValidateSet('CurrentUser', 'AllUsers')]
    [string] $Scope = 'CurrentUser',

    [ValidateNotNullOrEmpty()]
    [string] $RepositoryUrl = 'https://github.com/Kmac907/Dispatch.git',

    [ValidateNotNullOrEmpty()]
    [string] $Ref = 'main',

    [string] $SourceRoot = $null,

    [string] $WorkRoot = $null,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateNotNullOrEmpty()]
    [string] $Runtime = 'win-x64',

    [string] $DestinationRoot = $null,

    [switch] $Force,

    [switch] $NoCleanup,

    [switch] $NoRestore,

    [switch] $NoPathUpdate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedCommands = @(
    'Get-DispatchVersion',
    'Invoke-DispatchCommand',
    'Invoke-DispatchExecutable',
    'Invoke-DispatchJob',
    'Invoke-DispatchPowerShell',
    'Test-Dispatch'
)

function Resolve-FullPath {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    [System.IO.Path]::GetFullPath((Join-Path -Path (Get-Location) -ChildPath $Path))
}

function New-SourceCleanupResult {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('skipped', 'scheduled', 'scheduleFailed')]
        [string] $Status,

        [string] $HelperPath = $null,

        [string] $StatusPath = $null,

        [string] $ErrorMessage = $null
    )

    [pscustomobject]@{
        Status = $Status
        HelperPath = $HelperPath
        StatusPath = $StatusPath
        ErrorMessage = $ErrorMessage
    }
}

function Resolve-SafeCleanupTarget {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $resolved = Resolve-FullPath -Path $Path
    $root = [System.IO.Path]::GetPathRoot($resolved)
    $trimChars = [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )
    $normalized = $resolved.TrimEnd($trimChars)
    $normalizedRoot = $root.TrimEnd($trimChars)

    if ([string]::IsNullOrWhiteSpace($normalized) -or $normalized.Equals($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to schedule cleanup for unsafe path '$resolved'."
    }

    $resolved
}

function Test-DispatchSourceRoot {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $solutionPath = Join-Path -Path $Path -ChildPath 'Dispatch.sln'
    $buildScript = Join-Path -Path $Path -ChildPath 'packaging\build-module.ps1'
    $installScript = Join-Path -Path $Path -ChildPath 'packaging\install.ps1'

    return (
        (Test-Path -LiteralPath $solutionPath -PathType Leaf) -and
        (Test-Path -LiteralPath $buildScript -PathType Leaf) -and
        (Test-Path -LiteralPath $installScript -PathType Leaf)
    )
}

function Resolve-SourceRoot {
    if (-not [string]::IsNullOrWhiteSpace($SourceRoot)) {
        $resolved = Resolve-FullPath -Path $SourceRoot
        if (-not (Test-DispatchSourceRoot -Path $resolved)) {
            throw "SourceRoot '$resolved' is not a Dispatch checkout with the required packaging scripts."
        }

        return [pscustomobject]@{
            Path = $resolved
            IsTemporary = $false
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $candidate = [System.IO.Path]::GetFullPath((Join-Path -Path $PSScriptRoot -ChildPath '..'))
        if (Test-DispatchSourceRoot -Path $candidate) {
            return [pscustomobject]@{
                Path = $candidate
                IsTemporary = $false
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
        $WorkRoot = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath 'DispatchSourceInstall'
    }

    $resolvedWorkRoot = Resolve-FullPath -Path $WorkRoot
    New-Item -ItemType Directory -Path $resolvedWorkRoot -Force | Out-Null

    $cloneRoot = Join-Path -Path $resolvedWorkRoot -ChildPath ("Dispatch-" + [guid]::NewGuid().ToString('N'))
    $gitCommand = Get-Command git -ErrorAction SilentlyContinue
    if ($null -eq $gitCommand) {
        throw 'git was not found on PATH. Install Git or run this script from an existing checkout with -SourceRoot.'
    }

    & git clone --depth 1 --branch $Ref $RepositoryUrl $cloneRoot
    if ($LASTEXITCODE -ne 0) {
        throw "git clone failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-DispatchSourceRoot -Path $cloneRoot)) {
        throw "Cloned source '$cloneRoot' is not a Dispatch checkout with the required packaging scripts."
    }

    [pscustomobject]@{
        Path = $cloneRoot
        IsTemporary = $true
    }
}

function Start-SourceCleanup {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $helperPath = $null
    $statusPath = $null

    try {
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
            return New-SourceCleanupResult -Status 'skipped' -ErrorMessage 'Temporary source path was already removed.'
        }

        $resolvedPath = Resolve-SafeCleanupTarget -Path $Path
        $helperRoot = Resolve-FullPath -Path ([System.IO.Path]::GetTempPath())
        $pathPrefix = $resolvedPath.TrimEnd([char[]]@(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar
        )) + [System.IO.Path]::DirectorySeparatorChar

        if ($helperRoot.StartsWith($pathPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to create cleanup helper under cleanup target '$resolvedPath'."
        }

        New-Item -ItemType Directory -Path $helperRoot -Force | Out-Null
        $cleanupId = [guid]::NewGuid().ToString('N')
        $helperPath = Join-Path -Path $helperRoot -ChildPath "cleanup-dispatch-source-$PID-$cleanupId.ps1"
        $statusPath = Join-Path -Path $helperRoot -ChildPath "cleanup-dispatch-source-$PID-$cleanupId.status.json"
        $escapedPath = $resolvedPath.Replace("'", "''", [StringComparison]::Ordinal)
        $escapedHelper = $helperPath.Replace("'", "''", [StringComparison]::Ordinal)
        $escapedStatus = $statusPath.Replace("'", "''", [StringComparison]::Ordinal)

        Set-Content -LiteralPath $helperPath -Encoding UTF8 -Value @"
`$ErrorActionPreference = 'Continue'
`$statusPath = '$escapedStatus'

function Write-CleanupStatus {
    param(
        [Parameter(Mandatory)]
        [string] `$Status,

        [Parameter(Mandatory)]
        [string] `$Message
    )

    `$payload = [pscustomobject]@{
        status = `$Status
        message = `$Message
        path = '$escapedPath'
        timestamp = [DateTimeOffset]::UtcNow.ToString('o')
    }

    `$payload | ConvertTo-Json -Compress | Set-Content -LiteralPath `$statusPath -Encoding UTF8 -ErrorAction SilentlyContinue
}

try {
    Write-CleanupStatus -Status 'running' -Message 'Cleanup helper started.'
    Start-Sleep -Seconds 2
    if (Test-Path -LiteralPath '$escapedPath' -PathType Container) {
        Remove-Item -LiteralPath '$escapedPath' -Recurse -Force -ErrorAction Stop
    }

    Write-CleanupStatus -Status 'succeeded' -Message 'Temporary source checkout removed.'
}
catch {
    Write-CleanupStatus -Status 'failed' -Message `$_.Exception.Message
}
finally {
    Remove-Item -LiteralPath '$escapedHelper' -Force -ErrorAction SilentlyContinue
}
"@

        [pscustomobject]@{
            status = 'scheduled'
            message = 'Cleanup helper scheduled.'
            path = $resolvedPath
            timestamp = [DateTimeOffset]::UtcNow.ToString('o')
        } | ConvertTo-Json -Compress | Set-Content -LiteralPath $statusPath -Encoding UTF8

        $shell = (Get-Process -Id $PID).Path
        if ([string]::IsNullOrWhiteSpace($shell) -or -not (Test-Path -LiteralPath $shell -PathType Leaf)) {
            $shell = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh' } else { 'powershell.exe' }
        }

        Start-Process -FilePath $shell -ArgumentList @(
            '-NoProfile',
            '-ExecutionPolicy',
            'Bypass',
            '-File',
            $helperPath
        ) -WindowStyle Hidden | Out-Null

        New-SourceCleanupResult -Status 'scheduled' -HelperPath $helperPath -StatusPath $statusPath
    }
    catch {
        if (-not [string]::IsNullOrWhiteSpace($helperPath) -and (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
            Remove-Item -LiteralPath $helperPath -Force -ErrorAction SilentlyContinue
        }

        if (-not [string]::IsNullOrWhiteSpace($statusPath)) {
            [pscustomobject]@{
                status = 'scheduleFailed'
                message = $_.Exception.Message
                path = $Path
                timestamp = [DateTimeOffset]::UtcNow.ToString('o')
            } | ConvertTo-Json -Compress | Set-Content -LiteralPath $statusPath -Encoding UTF8 -ErrorAction SilentlyContinue
        }

        New-SourceCleanupResult -Status 'scheduleFailed' -HelperPath $helperPath -StatusPath $statusPath -ErrorMessage $_.Exception.Message
    }
}

$source = Resolve-SourceRoot
$buildScriptPath = Join-Path -Path $source.Path -ChildPath 'packaging\build-module.ps1'
$installScriptPath = Join-Path -Path $source.Path -ChildPath 'packaging\install.ps1'
$moduleOutputPath = Join-Path -Path $source.Path -ChildPath 'artifacts\module\Dispatch'

$buildArguments = @{
    Configuration = $Configuration
    Runtime = $Runtime
    OutputPath = $moduleOutputPath
}
if ($NoRestore) {
    $buildArguments.NoRestore = $true
}

$buildOutput = & $buildScriptPath @buildArguments
$buildResult = @(
    $buildOutput |
        Where-Object { $null -ne $_.PSObject.Properties['ModulePath'] } |
        Select-Object -Last 1
)
if ($null -eq $buildResult -or [string]::IsNullOrWhiteSpace($buildResult.ModulePath)) {
    throw 'Module build did not return a module output path.'
}

$installArguments = @{
    ModulePath = $buildResult.ModulePath
    Scope = $Scope
}
if (-not [string]::IsNullOrWhiteSpace($DestinationRoot)) {
    $installArguments.DestinationRoot = $DestinationRoot
}
$installArguments.Force = $true
if ($NoPathUpdate -or -not [string]::IsNullOrWhiteSpace($DestinationRoot)) {
    $installArguments.NoPathUpdate = $true
}

$installResult = & $installScriptPath @installArguments

$manifestPath = $installResult.ManifestPath
$dispatchPath = $installResult.DispatchPath
if ([string]::IsNullOrWhiteSpace($manifestPath) -or -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'Installed Dispatch module manifest was not found after installation.'
}

if ([string]::IsNullOrWhiteSpace($dispatchPath) -or -not (Test-Path -LiteralPath $dispatchPath -PathType Leaf)) {
    throw 'Installed bundled dispatch.exe was not found after installation.'
}

Remove-Module -Name Dispatch -Force -ErrorAction SilentlyContinue
$module = Import-Module -Name $manifestPath -Force -PassThru
$actualCommands = @($module.ExportedCommands.Keys | Sort-Object)
if (($actualCommands -join '|') -ne ($expectedCommands -join '|')) {
    throw "Unexpected installed Dispatch module exports: $($actualCommands -join ', ')."
}

$version = Get-DispatchVersion -DispatchPath $dispatchPath
if ($version.DispatchPath -ne (Resolve-FullPath -Path $dispatchPath)) {
    throw "Installed Get-DispatchVersion resolved '$($version.DispatchPath)' instead of '$dispatchPath'."
}

& $dispatchPath --help > $null
if ($LASTEXITCODE -ne 0) {
    throw "Installed dispatch.exe --help failed with exit code $LASTEXITCODE."
}

$cleanupResult = New-SourceCleanupResult -Status 'skipped'
if ($source.IsTemporary -and -not $NoCleanup) {
    try {
        $safeLocation = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
        if ([string]::IsNullOrWhiteSpace($safeLocation) -or -not (Test-Path -LiteralPath $safeLocation -PathType Container)) {
            $safeLocation = [System.IO.Path]::GetTempPath()
        }

        Set-Location -Path $safeLocation
        $cleanupResult = Start-SourceCleanup -Path $source.Path
    }
    catch {
        $cleanupResult = New-SourceCleanupResult -Status 'scheduleFailed' -ErrorMessage $_.Exception.Message
    }
}

[pscustomobject]@{
    SourceRoot = $source.Path
    SourceIsTemporary = $source.IsTemporary
    Scope = $installResult.Scope
    InstallPath = $installResult.InstallPath
    ManifestPath = $manifestPath
    DispatchPath = $dispatchPath
    DispatchPathEntry = $installResult.DispatchPathEntry
    PathUpdate = $installResult.PathUpdate
    PathTarget = $installResult.PathTarget
    ModuleVersion = $installResult.ModuleVersion
    DispatchVersion = $version.Version
    ExportedCommands = $actualCommands
    Cleanup = $cleanupResult.Status
    CleanupHelperPath = $cleanupResult.HelperPath
    CleanupStatusPath = $cleanupResult.StatusPath
    CleanupError = $cleanupResult.ErrorMessage
}
