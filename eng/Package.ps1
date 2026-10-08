param([string]$Version = '0.1.0', [switch]$Installer, [string]$Compiler)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part release version' }
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $dotnetTool = if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { Join-Path $projectRoot '.tools\dotnet\dotnet.exe' }
    $publishPath = Join-Path $projectRoot "artifacts\publish\$Version\win-x64"
    $releasePath = Join-Path $projectRoot 'artifacts\releases'
    if (Test-Path -LiteralPath (Join-Path $releasePath "DiskUsageAnalyzer-$Version-win-x64.zip")) { throw 'Release ZIP already exists; choose a new version or explicitly move the old artifact first' }
    New-Item -ItemType Directory -Path $releasePath -Force | Out-Null
    & $dotnetTool publish src/DiskUsageAnalyzer.App -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -p:RestoreLockedMode=true -p:Version=$Version -o $publishPath
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publishing failed' }
    foreach ($document in @('LICENSE','README.md','THIRD_PARTY_NOTICES.md','PRIVACY.md','CONTRIBUTING.md','CODE_OF_CONDUCT.md','SECURITY.md','CHANGELOG.md')) { Copy-Item -LiteralPath $document -Destination $publishPath }
    Copy-Item -LiteralPath docs -Destination $publishPath -Recurse -Force
    $licensePath = Join-Path $publishPath 'licenses'; New-Item -ItemType Directory -Path $licensePath -Force | Out-Null
    $assets = Get-Content -LiteralPath src/DiskUsageAnalyzer.App/obj/project.assets.json -Raw | ConvertFrom-Json -AsHashtable
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $publishPath 'DiskUsageAnalyzer.App.runtimeconfig.json') -Raw | ConvertFrom-Json
    $runtimeLibraries = @($runtimeConfig.runtimeOptions.includedFrameworks | ForEach-Object { "$($_.name).Runtime.win-x64/$($_.version)" })
    $upstreamVersions = Get-Content -LiteralPath third-party/versions.json -Raw | ConvertFrom-Json
    if (($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').version -ne $upstreamVersions.wpfVersion) { throw 'Update the vendored WPF notices for this runtime version before packaging' }
    if ($assets.libraries['SQLitePCLRaw.core/' + $upstreamVersions.sqlitePclVersion] -eq $null) { throw 'Update the vendored SQLitePCLRaw license version before packaging' }
    Copy-Item -Path third-party/*.txt -Destination $licensePath
    $allLibraries = @($assets.libraries.Keys | Where-Object { $assets.libraries[$_].type -eq 'package' }) + $runtimeLibraries
    foreach ($library in $allLibraries) {
        $runtimeLicenseFound = $false
        foreach ($cacheRoot in $assets.packageFolders.Keys) {
            $packageFolder = Join-Path $cacheRoot $library.ToLowerInvariant()
            if (Test-Path -LiteralPath $packageFolder) {
                Get-ChildItem -LiteralPath $packageFolder -File | Where-Object { $_.Name -match '^(license|third.?party)' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licensePath (($library -replace '[\\/]', '-') + '-' + $_.Name)); $runtimeLicenseFound = $true }
            }
        }
        if ($library -in $runtimeLibraries -and !$runtimeLicenseFound) { throw "Runtime license not found for $library" }
    }
    # Offline dependency inventory from checked-in lockfiles; native/runtime notices are included separately.
    $packages = @{}
    Get-ChildItem src -Recurse -Filter packages.lock.json | ForEach-Object {
        $lock = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json -AsHashtable
        foreach ($target in $lock.dependencies.Values) { foreach ($name in $target.Keys) { if ($target[$name].resolved) { $key = "$name@$($target[$name].resolved)"; $packages[$key] = @{ name=$name; version=$target[$name].resolved; contentHash=$target[$name].contentHash } } } }
    }
    foreach ($library in $runtimeLibraries) {
        $name,$runtimeVersion = $library -split '/',2
        $hashFile = @($assets.packageFolders.Keys | ForEach-Object { Join-Path (Join-Path $_ $library.ToLowerInvariant()) ($name.ToLowerInvariant() + '.' + $runtimeVersion + '.nupkg.sha512') } | Where-Object { Test-Path -LiteralPath $_ }) | Select-Object -First 1
        if (!$hashFile) { throw "Runtime integrity hash missing for $library" }
        $packages["$name@$runtimeVersion"] = @{ name=$name; version=$runtimeVersion; contentHash=([IO.File]::ReadAllText($hashFile).Trim()) }
    }
    $inventory = @{ format='DiskUsageAnalyzer dependency inventory v1'; applicationVersion=$Version; runtime='self-contained .NET 10, Windows x64'; includedFrameworks=@($runtimeConfig.runtimeOptions.includedFrameworks); packages=@($packages.Values | Sort-Object name,version) }
    $inventory | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $publishPath 'dependencies.json') -Encoding utf8
    Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath (Join-Path $releasePath "DiskUsageAnalyzer-$Version-win-x64.zip")
    if ($Installer) {
        if (-not $Compiler) { $Compiler = Join-Path $projectRoot '.tools\innosetup\ISCC.exe' }
        if (-not (Test-Path -LiteralPath $Compiler)) { throw 'Inno Setup compiler missing. Install the pinned compiler or pass -Compiler.' }
        & $Compiler '/Qp' "/DAppVersion=$Version" "/DSourceDir=$publishPath" "/DOutputDir=$releasePath" (Join-Path $PSScriptRoot 'Installer.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
    }
    Get-ChildItem -LiteralPath $releasePath -File | Where-Object { $_.Name -like "DiskUsageAnalyzer-$Version-*" } | ForEach-Object { $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256; "$($hash.Hash.ToLowerInvariant())  $($_.Name)" } | Set-Content -LiteralPath (Join-Path $releasePath "SHA256SUMS-$Version.txt") -Encoding utf8
    Write-Output "Unsigned self-contained release artifacts: $releasePath"
} finally { Pop-Location }
