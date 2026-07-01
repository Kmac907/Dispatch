[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $ModulePath,

    [ValidateSet('CurrentUser', 'AllUsers')]
    [string] $Scope = 'CurrentUser',

    [ValidateNotNullOrEmpty()]
    [string] $DestinationRoot,

    [switch] $Force,

    [switch] $NoPathUpdate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$moduleName = 'Dispatch'
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

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-DefaultModuleRoot {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('CurrentUser', 'AllUsers')]
        [string] $InstallScope
    )

    $isCore = $PSVersionTable.ContainsKey('PSEdition') -and $PSVersionTable.PSEdition -eq 'Core'
    if ($InstallScope -eq 'CurrentUser') {
        $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
        if ([string]::IsNullOrWhiteSpace($documents)) {
            throw 'The current user Documents folder could not be resolved.'
        }

        if ($isCore) {
            return (Join-Path -Path $documents -ChildPath 'PowerShell\Modules')
        }

        return (Join-Path -Path $documents -ChildPath 'WindowsPowerShell\Modules')
    }

    if (-not (Test-IsAdministrator)) {
        throw 'Scope AllUsers requires an elevated PowerShell session.'
    }

    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    if ([string]::IsNullOrWhiteSpace($programFiles)) {
        throw 'Program Files could not be resolved.'
    }

    if ($isCore) {
        return (Join-Path -Path $programFiles -ChildPath 'PowerShell\Modules')
    }

    return (Join-Path -Path $programFiles -ChildPath 'WindowsPowerShell\Modules')
}

function Test-DispatchModulePackage {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Module package folder was not found at '$Path'."
    }

    $manifestPath = Join-Path -Path $Path -ChildPath 'Dispatch.psd1'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Dispatch module manifest was not found at '$manifestPath'."
    }

    $dispatchPath = Join-Path -Path $Path -ChildPath 'bin\win-x64\dispatch.exe'
    if (-not (Test-Path -LiteralPath $dispatchPath -PathType Leaf)) {
        throw "Bundled dispatch executable was not found at '$dispatchPath'."
    }

    $manifest = Test-ModuleManifest -Path $manifestPath

    Remove-Module -Name Dispatch -Force -ErrorAction SilentlyContinue
    $module = Import-Module -Name $manifestPath -Force -PassThru
    $actualCommands = @($module.ExportedCommands.Keys | Sort-Object)
    if (($actualCommands -join '|') -ne ($expectedCommands -join '|')) {
        throw "Unexpected exported commands from '$manifestPath': $($actualCommands -join ', ')."
    }

    $version = Get-DispatchVersion -DispatchPath $dispatchPath
    if ($version.DispatchPath -ne (Resolve-FullPath -Path $dispatchPath)) {
        throw "Get-DispatchVersion resolved '$($version.DispatchPath)' instead of '$dispatchPath'."
    }

    if ($version.Version -ne $manifest.Version.ToString()) {
        throw "Dispatch executable version '$($version.Version)' does not match module version '$($manifest.Version)'."
    }

    [pscustomobject]@{
        ManifestPath = $manifestPath
        DispatchPath = $dispatchPath
        ModuleVersion = $manifest.Version.ToString()
        ExportedCommands = $actualCommands
        DispatchVersion = $version.Version
    }
}

function Get-PathTarget {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('CurrentUser', 'AllUsers')]
        [string] $InstallScope
    )

    if ($InstallScope -eq 'AllUsers') {
        return [EnvironmentVariableTarget]::Machine
    }

    [EnvironmentVariableTarget]::User
}

function Split-PathEntries {
    param(
        [string] $PathValue
    )

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return @()
    }

    @(
        $PathValue.Split([System.IO.Path]::PathSeparator, [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
}

function Test-PathEntryPresent {
    param(
        [Parameter(Mandatory)]
        [string[]] $Entries,

        [Parameter(Mandatory)]
        [string] $Entry
    )

    $resolvedEntry = Resolve-FullPath -Path $Entry
    foreach ($existing in $Entries) {
        try {
            if ((Resolve-FullPath -Path $existing).Equals($resolvedEntry, [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
        catch {
            if ($existing.Equals($Entry, [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
    }

    $false
}

function Add-DispatchToPath {
    param(
        [Parameter(Mandatory)]
        [string] $BinPath,

        [Parameter(Mandatory)]
        [ValidateSet('CurrentUser', 'AllUsers')]
        [string] $InstallScope
    )

    $resolvedBinPath = Resolve-FullPath -Path $BinPath
    if (-not (Test-Path -LiteralPath $resolvedBinPath -PathType Container)) {
        throw "Dispatch executable folder was not found at '$resolvedBinPath'."
    }

    $target = Get-PathTarget -InstallScope $InstallScope
    $persistedPath = [Environment]::GetEnvironmentVariable('Path', $target)
    $persistedEntries = Split-PathEntries -PathValue $persistedPath
    $wasPresent = Test-PathEntryPresent -Entries $persistedEntries -Entry $resolvedBinPath

    if (-not $wasPresent) {
        $newPersistedEntries = @($resolvedBinPath) + $persistedEntries
        [Environment]::SetEnvironmentVariable('Path', ($newPersistedEntries -join [System.IO.Path]::PathSeparator), $target)
    }

    $processEntries = Split-PathEntries -PathValue $env:Path
    if (-not (Test-PathEntryPresent -Entries $processEntries -Entry $resolvedBinPath)) {
        $env:Path = (@($resolvedBinPath) + $processEntries) -join [System.IO.Path]::PathSeparator
    }

    $command = Get-Command -Name dispatch.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) {
        throw "dispatch.exe was not found on PATH after adding '$resolvedBinPath'."
    }

    $resolvedCommand = Resolve-FullPath -Path $command.Source
    $expectedCommand = Resolve-FullPath -Path (Join-Path -Path $resolvedBinPath -ChildPath 'dispatch.exe')
    if (-not $resolvedCommand.Equals($expectedCommand, [StringComparison]::OrdinalIgnoreCase)) {
        throw "dispatch.exe resolved to '$resolvedCommand' instead of the installed executable '$expectedCommand'."
    }

    & dispatch.exe --help > $null
    if ($LASTEXITCODE -ne 0) {
        throw "Installed dispatch.exe --help failed through PATH with exit code $LASTEXITCODE."
    }

    [pscustomobject]@{
        Status = if ($wasPresent) { 'alreadyPresent' } else { 'updated' }
        Target = $target.ToString()
        Path = $resolvedBinPath
    }
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path -Path $PSScriptRoot -ChildPath '..'))
if ([string]::IsNullOrWhiteSpace($ModulePath)) {
    $ModulePath = Join-Path -Path $repoRoot -ChildPath 'artifacts\module\Dispatch'
}

$resolvedModulePath = Resolve-FullPath -Path $ModulePath
$sourceValidation = Test-DispatchModulePackage -Path $resolvedModulePath

if ([string]::IsNullOrWhiteSpace($DestinationRoot)) {
    $DestinationRoot = Get-DefaultModuleRoot -InstallScope $Scope
}

$resolvedDestinationRoot = Resolve-FullPath -Path $DestinationRoot
$moduleRoot = Join-Path -Path $resolvedDestinationRoot -ChildPath $moduleName
$installPath = Join-Path -Path $moduleRoot -ChildPath $sourceValidation.ModuleVersion
$stagingPath = Join-Path -Path $moduleRoot -ChildPath ".$($sourceValidation.ModuleVersion).installing.$PID"

if (Test-Path -LiteralPath $installPath) {
    if (-not $Force) {
        throw "Dispatch module version '$($sourceValidation.ModuleVersion)' is already installed at '$installPath'. Use -Force to replace it."
    }

    Remove-Item -LiteralPath $installPath -Recurse -Force
}

if (Test-Path -LiteralPath $stagingPath) {
    Remove-Item -LiteralPath $stagingPath -Recurse -Force
}

New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
try {
    Get-ChildItem -LiteralPath $resolvedModulePath -Force |
        Copy-Item -Destination $stagingPath -Recurse -Force

    Move-Item -LiteralPath $stagingPath -Destination $installPath
    $installedValidation = Test-DispatchModulePackage -Path $installPath
    $dispatchBinPath = Split-Path -Parent -Path $installedValidation.DispatchPath
    $pathUpdate = if ($NoPathUpdate) {
        [pscustomobject]@{
            Status = 'skipped'
            Target = $null
            Path = $dispatchBinPath
        }
    }
    else {
        Add-DispatchToPath -BinPath $dispatchBinPath -InstallScope $Scope
    }

    [pscustomobject]@{
        ModuleName = $moduleName
        Scope = $Scope
        ModuleRoot = $resolvedDestinationRoot
        InstallPath = $installPath
        ManifestPath = $installedValidation.ManifestPath
        DispatchPath = $installedValidation.DispatchPath
        DispatchPathEntry = $pathUpdate.Path
        PathUpdate = $pathUpdate.Status
        PathTarget = $pathUpdate.Target
        ModuleVersion = $installedValidation.ModuleVersion
        DispatchVersion = $installedValidation.DispatchVersion
        ExportedCommands = $installedValidation.ExportedCommands
    }
}
catch {
    if (Test-Path -LiteralPath $installPath) {
        Remove-Item -LiteralPath $installPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    throw
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}
