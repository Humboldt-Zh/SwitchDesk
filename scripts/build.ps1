param([string]$Dotnet = 'dotnet', [string]$Python = 'python', [switch]$FrameworkDependent, [string]$TestDirectory, [switch]$SkipUiCheck)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
& $Python "$taskRoot\scripts\build-info.py"
if ($LASTEXITCODE) { throw "Build identity generation failed" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $Dotnet build "$taskRoot\src\SwitchDesk.App\SwitchDesk.App.csproj" -c Release --nologo
if ($LASTEXITCODE) { throw 'Windows build failed' }
$testArguments = @('run','--project',"$taskRoot\tests\SwitchDesk.Tests\SwitchDesk.Tests.csproj",'-c','Release')
if ($TestDirectory) { $testArguments += @('--', $TestDirectory) }
& $Dotnet @testArguments
if ($LASTEXITCODE) { throw 'Tests failed' }
$contained = if ($FrameworkDependent) { 'false' } else { 'true' }
& $Dotnet publish "$taskRoot\src\SwitchDesk.App\SwitchDesk.App.csproj" -c Release -r win-x64 --self-contained $contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$taskRoot\artifacts\Windows" --nologo
if ($LASTEXITCODE) { throw 'Publish failed' }
if (!$SkipUiCheck) {
    $uiRoot = if ($TestDirectory) { Join-Path $TestDirectory 'ui' } else { Join-Path $taskRoot 'artifacts\UiSmoke' }
    $uiData = Join-Path $uiRoot 'data'
    $uiImages = Join-Path $uiRoot ('images-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $uiRoot -Force | Out-Null
    $priorBundleCache = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $uiRoot 'bundle-cache'
    try {
        $uiArguments = @('--data-dir', ('"' + $uiData + '"'), '--smoke-test', ('"' + $uiImages + '"'))
        $uiProcess = Start-Process -FilePath "$taskRoot\artifacts\Windows\SwitchDesk.exe" -ArgumentList $uiArguments -PassThru -WindowStyle Hidden
        if (!$uiProcess.WaitForExit(30000)) { throw 'UI smoke test timed out' }
        if ($uiProcess.ExitCode -ne 0) {
            if (Test-Path -LiteralPath "$uiImages\error.txt") { Get-Content -LiteralPath "$uiImages\error.txt" | Write-Output }
            throw 'UI smoke test failed'
        }
        if (!(Test-Path -LiteralPath "$uiImages\matrix-Dark-1050-200.png")) { throw 'UI smoke test did not render every page' }
        Write-Output "UI smoke passed: $uiImages"
    }
    finally { $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $priorBundleCache }
}
Copy-Item -LiteralPath "$taskRoot\README.md", "$taskRoot\LICENSE", "$taskRoot\THIRD-PARTY-NOTICES.md" -Destination "$taskRoot\artifacts\Windows"
Copy-Item -LiteralPath "$taskRoot\build-info.json" -Destination "$taskRoot\artifacts\Windows"
New-Item -ItemType Directory -Path "$taskRoot\artifacts\Windows\docs" -Force | Out-Null
Get-ChildItem -LiteralPath "$taskRoot\docs" -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination "$taskRoot\artifacts\Windows\docs" -Force }
$licenseTarget = "$taskRoot\artifacts\Windows\licenses"
New-Item -ItemType Directory -Path $licenseTarget -Force | Out-Null
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages' }
foreach ($package in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
    $packPath = Join-Path $nugetRoot $package
    if (Test-Path -LiteralPath $packPath) {
        $version = Get-ChildItem -LiteralPath $packPath -Directory | Where-Object Name -Match '^10\.' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        if ($version) {
            Get-ChildItem -LiteralPath $version.FullName -File | Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)' | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenseTarget ($package + '-' + $_.Name))
            }
        }
    }
}
