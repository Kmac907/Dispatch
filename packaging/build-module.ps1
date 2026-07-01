[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateNotNullOrEmpty()]
    [string] $Runtime = 'win-x64',

    [ValidateNotNullOrEmpty()]
    [string] $OutputPath,

    [ValidateNotNullOrEmpty()]
    [string] $PackageOutputPath,

    [switch] $CreateZip,

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

function Test-DispatchHelp {
    param(
        [Parameter(Mandatory)]
        [string] $DispatchPath
    )

    $helpOutput = & $DispatchPath --help 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Bundled dispatch executable help failed with exit code $LASTEXITCODE. Output: $($helpOutput -join [Environment]::NewLine)"
    }
}

function New-DispatchZipPackage {
    param(
        [Parameter(Mandatory)]
        [string] $ModulePath,

        [Parameter(Mandatory)]
        [string] $DispatchPath,

        [Parameter(Mandatory)]
        [string] $InstallerPath,

        [Parameter(Mandatory)]
        [string] $PackageDirectory,

        [Parameter(Mandatory)]
        [string] $Runtime,

        [Parameter(Mandatory)]
        [string] $Version
    )

    $resolvedPackageDirectory = Resolve-FullPath -Path $PackageDirectory
    New-Item -ItemType Directory -Path $resolvedPackageDirectory -Force | Out-Null

    $zipPath = Join-Path -Path $resolvedPackageDirectory -ChildPath "Dispatch-$Version-$Runtime.zip"
    $stagingRoot = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath "dispatch-package-$([Guid]::NewGuid().ToString('N'))"
    $validationRoot = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath "dispatch-package-validation-$([Guid]::NewGuid().ToString('N'))"

    try {
        $stagedModuleRoot = Join-Path -Path $stagingRoot -ChildPath 'Dispatch'
        $stagedBinRoot = Join-Path -Path $stagedModuleRoot -ChildPath "bin\$Runtime"
        New-Item -ItemType Directory -Path $stagedBinRoot -Force | Out-Null

        Copy-Item -LiteralPath (Join-Path -Path $ModulePath -ChildPath 'Dispatch.psd1') -Destination $stagedModuleRoot -Force
        Copy-Item -LiteralPath (Join-Path -Path $ModulePath -ChildPath 'Dispatch.psm1') -Destination $stagedModuleRoot -Force
        Copy-Item -LiteralPath $InstallerPath -Destination (Join-Path -Path $stagedModuleRoot -ChildPath 'install.ps1') -Force
        Copy-Item -LiteralPath $DispatchPath -Destination (Join-Path -Path $stagedBinRoot -ChildPath 'dispatch.exe') -Force

        if (Test-Path -LiteralPath $zipPath) {
            Remove-Item -LiteralPath $zipPath -Force
        }

        Compress-Archive -LiteralPath $stagedModuleRoot -DestinationPath $zipPath -CompressionLevel Optimal -Force

        Expand-Archive -LiteralPath $zipPath -DestinationPath $validationRoot -Force
        $extractedModuleRoot = Join-Path -Path $validationRoot -ChildPath 'Dispatch'
        $extractedInstaller = Join-Path -Path $extractedModuleRoot -ChildPath 'install.ps1'
        if (-not (Test-Path -LiteralPath $extractedInstaller -PathType Leaf)) {
            throw "ZIP validation failed because '$extractedInstaller' was not found."
        }

        $validationInstallRoot = Join-Path -Path $validationRoot -ChildPath 'InstalledModules'
        $installed = & $extractedInstaller -ModulePath $extractedModuleRoot -DestinationRoot $validationInstallRoot -Force
        if ($LASTEXITCODE -ne 0) {
            throw "ZIP validation install failed with exit code $LASTEXITCODE."
        }

        if ($null -eq $installed -or [string]::IsNullOrWhiteSpace($installed.DispatchPath)) {
            throw 'ZIP validation install did not return a bundled dispatch executable path.'
        }

        Test-DispatchHelp -DispatchPath $installed.DispatchPath

        return $zipPath
    }
    finally {
        if (Test-Path -LiteralPath $stagingRoot) {
            Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        if (Test-Path -LiteralPath $validationRoot) {
            Remove-Item -LiteralPath $validationRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path -Path $PSScriptRoot -ChildPath '..'))
$moduleSource = Join-Path -Path $repoRoot -ChildPath 'module\Dispatch'
$cliProject = Join-Path -Path $repoRoot -ChildPath 'src\Dispatch.Cli\Dispatch.Cli.csproj'
$packageInstaller = Join-Path -Path $repoRoot -ChildPath 'packaging\install.ps1'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path -Path $repoRoot -ChildPath 'artifacts\module\Dispatch'
}

if ([string]::IsNullOrWhiteSpace($PackageOutputPath)) {
    $PackageOutputPath = Join-Path -Path $repoRoot -ChildPath 'artifacts\packages'
}

$resolvedOutputPath = Resolve-FullPath -Path $OutputPath
$publishPath = Join-Path -Path $repoRoot -ChildPath "artifacts\publish\Dispatch.Cli\$Runtime"

if (-not (Test-Path -LiteralPath $moduleSource -PathType Container)) {
    throw "Module source folder was not found at '$moduleSource'."
}

if (-not (Test-Path -LiteralPath $cliProject -PathType Leaf)) {
    throw "CLI project was not found at '$cliProject'."
}

if (-not (Test-Path -LiteralPath $packageInstaller -PathType Leaf)) {
    throw "Package installer was not found at '$packageInstaller'."
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
Copy-Item -LiteralPath $packageInstaller -Destination (Join-Path -Path $resolvedOutputPath -ChildPath 'install.ps1') -Force

$bundledExecutableFolder = Join-Path -Path $resolvedOutputPath -ChildPath "bin\$Runtime"
New-Item -ItemType Directory -Path $bundledExecutableFolder -Force | Out-Null
$bundledExecutable = Join-Path -Path $bundledExecutableFolder -ChildPath 'dispatch.exe'
Copy-Item -LiteralPath $publishedExecutable -Destination $bundledExecutable -Force

$manifestPath = Join-Path -Path $resolvedOutputPath -ChildPath 'Dispatch.psd1'
$manifest = Test-ModuleManifest -Path $manifestPath

Remove-Module -Name Dispatch -Force -ErrorAction SilentlyContinue
$module = Import-Module -Name $manifestPath -Force -PassThru
$version = Get-DispatchVersion
$packagePath = $null

if ($CreateZip) {
    $packagePath = New-DispatchZipPackage `
        -ModulePath $resolvedOutputPath `
        -DispatchPath $bundledExecutable `
        -InstallerPath (Join-Path -Path $resolvedOutputPath -ChildPath 'install.ps1') `
        -PackageDirectory $PackageOutputPath `
        -Runtime $Runtime `
        -Version $manifest.Version.ToString()
}

[pscustomobject]@{
    ModulePath = $resolvedOutputPath
    DispatchPath = $bundledExecutable
    PackagePath = $packagePath
    Runtime = $Runtime
    Configuration = $Configuration
    ModuleVersion = $manifest.Version.ToString()
    ExportedCommands = ($module.ExportedCommands.Keys | Sort-Object)
    DispatchVersion = $version.Version
}
