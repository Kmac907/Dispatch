[CmdletBinding()]
param(
    [ValidateSet('CurrentUser', 'AllUsers')]
    [string] $Scope = 'CurrentUser',

    [ValidateNotNullOrEmpty()]
    [string] $RepositoryUrl = 'https://github.com/Kmac907/Dispatch.git',

    [ValidateNotNullOrEmpty()]
    [string] $Ref = 'main',

    [ValidateNotNullOrEmpty()]
    [string] $SourceRoot,

    [ValidateNotNullOrEmpty()]
    [string] $WorkRoot,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateNotNullOrEmpty()]
    [string] $Runtime = 'win-x64',

    [ValidateNotNullOrEmpty()]
    [string] $DestinationRoot,

    [switch] $Force,

    [switch] $NoCleanup,

    [switch] $NoRestore
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

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return $null
    }

    $parent = Split-Path -Path $Path -Parent
    $helperPath = Join-Path -Path $parent -ChildPath ("cleanup-dispatch-source-$PID.ps1")
    $escapedPath = $Path.Replace("'", "''", [StringComparison]::Ordinal)
    $escapedHelper = $helperPath.Replace("'", "''", [StringComparison]::Ordinal)

    Set-Content -LiteralPath $helperPath -Encoding UTF8 -Value @"
`$ErrorActionPreference = 'Continue'
Start-Sleep -Seconds 2
Remove-Item -LiteralPath '$escapedPath' -Recurse -Force -ErrorAction Continue
Remove-Item -LiteralPath '$escapedHelper' -Force -ErrorAction Continue
"@

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

    $helperPath
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
if ($Force) {
    $installArguments.Force = $true
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

$cleanupHelperPath = $null
if ($source.IsTemporary -and -not $NoCleanup) {
    Set-Location -Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile))
    $cleanupHelperPath = Start-SourceCleanup -Path $source.Path
}

[pscustomobject]@{
    SourceRoot = $source.Path
    SourceIsTemporary = $source.IsTemporary
    Scope = $installResult.Scope
    InstallPath = $installResult.InstallPath
    ManifestPath = $manifestPath
    DispatchPath = $dispatchPath
    ModuleVersion = $installResult.ModuleVersion
    DispatchVersion = $version.Version
    ExportedCommands = $actualCommands
    Cleanup = if ($source.IsTemporary -and -not $NoCleanup) { 'scheduled' } else { 'skipped' }
    CleanupHelperPath = $cleanupHelperPath
}
