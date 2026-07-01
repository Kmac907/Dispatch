[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $ModulePath,

    [ValidateSet('CurrentUser', 'AllUsers')]
    [string] $Scope = 'CurrentUser',

    [ValidateNotNullOrEmpty()]
    [string] $DestinationRoot,

    [switch] $Force
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

    [pscustomobject]@{
        ModuleName = $moduleName
        Scope = $Scope
        ModuleRoot = $resolvedDestinationRoot
        InstallPath = $installPath
        ManifestPath = $installedValidation.ManifestPath
        DispatchPath = $installedValidation.DispatchPath
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
