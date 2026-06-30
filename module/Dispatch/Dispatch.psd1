@{
    RootModule = 'Dispatch.psm1'
    ModuleVersion = '0.1.0'
    GUID = 'b0bb3a89-8ef7-47a0-a6d5-7f55625392ef'
    Author = 'Dispatch'
    CompanyName = 'Dispatch'
    Copyright = '(c) Dispatch contributors. All rights reserved.'
    Description = 'PowerShell wrapper commands for dispatch.exe.'
    PowerShellVersion = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')
    FunctionsToExport = @(
        'Get-DispatchVersion',
        'Test-Dispatch'
    )
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @()
    PrivateData = @{
        PSData = @{
            Tags = @('Dispatch', 'Automation', 'Windows')
            ProjectUri = 'https://github.com/Kmac907/Dispatch'
        }
    }
}
