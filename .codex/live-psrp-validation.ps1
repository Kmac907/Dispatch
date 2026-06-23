$ErrorActionPreference = 'Stop'
Set-Location 'C:\Code\Work\Endpoint-Engineering\Dispatch'

$resultPath = Join-Path $PWD '.codex\live-psrp-validation-result.json'
$errorPath = Join-Path $PWD '.codex\live-psrp-validation-error.txt'
Remove-Item -LiteralPath $resultPath -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $errorPath -ErrorAction SilentlyContinue

try {
    $output = dotnet run --project .\src\Dispatch.Cli\Dispatch.Cli.csproj -- run cmd whoami --target 82H9704,92H9704 --transport psrp --no-progress --output json
    $output | Set-Content -LiteralPath $resultPath -Encoding UTF8
}
catch {
    $_ | Out-String | Set-Content -LiteralPath $errorPath -Encoding UTF8
    if ($output) {
        $output | Set-Content -LiteralPath $resultPath -Encoding UTF8
    }
    exit 1
}
