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

function Get-RawGitHubFileUri {
    param(
        [Parameter(Mandatory)]
        [string] $GitRepositoryUrl,

        [Parameter(Mandatory)]
        [string] $GitRef
    )

    $normalized = $GitRepositoryUrl.TrimEnd('/')
    if ($normalized.EndsWith('.git', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(0, $normalized.Length - 4)
    }

    $githubPrefix = 'https://github.com/'
    if (-not $normalized.StartsWith($githubPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "bootstrap-install.ps1 can download install-from-source.ps1 only from HTTPS GitHub repositories. Run from an existing checkout or pass -SourceRoot for '$GitRepositoryUrl'."
    }

    $repoPath = $normalized.Substring($githubPrefix.Length)
    if ([string]::IsNullOrWhiteSpace($repoPath) -or -not $repoPath.Contains('/')) {
        throw "RepositoryUrl '$GitRepositoryUrl' is not a valid GitHub owner/repository URL."
    }

    $escapedRef = [System.Uri]::EscapeDataString($GitRef)
    "https://raw.githubusercontent.com/$repoPath/$escapedRef/packaging/install-from-source.ps1"
}

function Resolve-InstallFromSourceScript {
    if (-not [string]::IsNullOrWhiteSpace($SourceRoot)) {
        $sourceInstaller = Join-Path -Path (Resolve-FullPath -Path $SourceRoot) -ChildPath 'packaging\install-from-source.ps1'
        if (Test-Path -LiteralPath $sourceInstaller -PathType Leaf) {
            return [pscustomobject]@{
                Path = $sourceInstaller
                IsTemporary = $false
            }
        }

        throw "SourceRoot '$SourceRoot' does not contain packaging\install-from-source.ps1."
    }

    $localInstaller = $null
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $localInstaller = Join-Path -Path $PSScriptRoot -ChildPath 'install-from-source.ps1'
        if (Test-Path -LiteralPath $localInstaller -PathType Leaf) {
            return [pscustomobject]@{
                Path = (Resolve-FullPath -Path $localInstaller)
                IsTemporary = $false
            }
        }
    }

    $installerUri = Get-RawGitHubFileUri -GitRepositoryUrl $RepositoryUrl -GitRef $Ref
    $tempRoot = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath 'DispatchBootstrap'
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    $tempInstaller = Join-Path -Path $tempRoot -ChildPath "install-from-source-$([Guid]::NewGuid().ToString('N')).ps1"

    Invoke-WebRequest -Uri $installerUri -OutFile $tempInstaller -UseBasicParsing

    [pscustomobject]@{
        Path = $tempInstaller
        IsTemporary = $true
    }
}

$installer = Resolve-InstallFromSourceScript

$forwardedParameters = @{}
foreach ($parameter in $PSBoundParameters.GetEnumerator()) {
    $forwardedParameters[$parameter.Key] = $parameter.Value
}

try {
    & $installer.Path @forwardedParameters
}
finally {
    if ($installer.IsTemporary -and (Test-Path -LiteralPath $installer.Path -PathType Leaf)) {
        Remove-Item -LiteralPath $installer.Path -Force -ErrorAction SilentlyContinue
    }
}
