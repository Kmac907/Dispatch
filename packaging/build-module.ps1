[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateNotNullOrEmpty()]
    [string] $Runtime = 'win-x64',

    [ValidateNotNullOrEmpty()]
    [string] $OutputPath,

    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path -Path $PSScriptRoot -ChildPath '..'))
$moduleSource = Join-Path -Path $repoRoot -ChildPath 'module\Dispatch'
$cliProject = Join-Path -Path $repoRoot -ChildPath 'src\Dispatch.Cli\Dispatch.Cli.csproj'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path -Path $repoRoot -ChildPath 'artifacts\module\Dispatch'
}

$resolvedOutputPath = Resolve-FullPath -Path $OutputPath
$publishPath = Join-Path -Path $repoRoot -ChildPath "artifacts\publish\Dispatch.Cli\$Runtime"

if (-not (Test-Path -LiteralPath $moduleSource -PathType Container)) {
    throw "Module source folder was not found at '$moduleSource'."
}

if (-not (Test-Path -LiteralPath $cliProject -PathType Leaf)) {
    throw "CLI project was not found at '$cliProject'."
}

if (Test-Path -LiteralPath $publishPath) {
    Remove-Item -LiteralPath $publishPath -Recurse -Force
}

New-Item -ItemType Directory -Path $publishPath -Force | Out-Null

$publishArguments = @(
    'publish',
    $cliProject,
    '--configuration',
    $Configuration,
    '--runtime',
    $Runtime,
    '--self-contained',
    'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-o',
    $publishPath
)

if ($NoRestore) {
    $publishArguments += '--no-restore'
}

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$publishedExecutable = Join-Path -Path $publishPath -ChildPath 'Dispatch.Cli.exe'
if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "Published CLI executable was not found at '$publishedExecutable'."
}

New-Item -ItemType Directory -Path $resolvedOutputPath -Force | Out-Null
Copy-Item -LiteralPath (Join-Path -Path $moduleSource -ChildPath 'Dispatch.psd1') -Destination $resolvedOutputPath -Force
Copy-Item -LiteralPath (Join-Path -Path $moduleSource -ChildPath 'Dispatch.psm1') -Destination $resolvedOutputPath -Force

$bundledExecutableFolder = Join-Path -Path $resolvedOutputPath -ChildPath "bin\$Runtime"
New-Item -ItemType Directory -Path $bundledExecutableFolder -Force | Out-Null
$bundledExecutable = Join-Path -Path $bundledExecutableFolder -ChildPath 'dispatch.exe'
Copy-Item -LiteralPath $publishedExecutable -Destination $bundledExecutable -Force

$manifestPath = Join-Path -Path $resolvedOutputPath -ChildPath 'Dispatch.psd1'
$manifest = Test-ModuleManifest -Path $manifestPath

Remove-Module -Name Dispatch -Force -ErrorAction SilentlyContinue
$module = Import-Module -Name $manifestPath -Force -PassThru
$version = Get-DispatchVersion

[pscustomobject]@{
    ModulePath = $resolvedOutputPath
    DispatchPath = $bundledExecutable
    Runtime = $Runtime
    Configuration = $Configuration
    ModuleVersion = $manifest.Version.ToString()
    ExportedCommands = ($module.ExportedCommands.Keys | Sort-Object)
    DispatchVersion = $version.Version
}
