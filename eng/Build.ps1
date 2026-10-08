param([switch]$UiTests, [switch]$Coverage)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $dotnetTool = if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { Join-Path $projectRoot '.tools\dotnet\dotnet.exe' }
    & $dotnetTool restore DiskUsageAnalyzer.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed' }
    & $dotnetTool build DiskUsageAnalyzer.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $testArgs = @('test', 'tests/DiskUsageAnalyzer.Tests', '-c', 'Release', '--no-build', '--no-restore', '--results-directory', 'artifacts/test-results')
    if ($Coverage) { $testArgs += @('--collect:XPlat Code Coverage') }
    & $dotnetTool @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Safety/unit/integration test gate failed' }
    if ($UiTests) {
        & $dotnetTool run --project tests/DiskUsageAnalyzer.UITests -c Release --no-build --no-restore -- artifacts/ui
        if ($LASTEXITCODE -ne 0) { throw 'Actual WPF workflow gate failed' }
    }
} finally { Pop-Location }
