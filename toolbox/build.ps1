$ErrorActionPreference = 'Stop'

# 1. Check dependencies
$webviewCore = Join-Path $PSScriptRoot 'Microsoft.Web.WebView2.Core.dll'
$webviewForms = Join-Path $PSScriptRoot 'Microsoft.Web.WebView2.WinForms.dll'
if (-not (Test-Path -LiteralPath $webviewCore) -or -not (Test-Path -LiteralPath $webviewForms) -or -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'WebView2Loader.dll'))) {
    & (Join-Path $PSScriptRoot 'prepare-webview2.ps1')
}

$naudioCore = Join-Path $PSScriptRoot 'NAudio.Core.dll'
$naudioWasapi = Join-Path $PSScriptRoot 'NAudio.Wasapi.dll'
if (-not (Test-Path -LiteralPath $naudioCore) -or -not (Test-Path -LiteralPath $naudioWasapi)) {
    & (Join-Path $PSScriptRoot 'prepare-naudio.ps1')
}

$output = Join-Path $PSScriptRoot '网易云工具箱.exe'
$backupDir = Join-Path $PSScriptRoot 'backups'
if (-not (Test-Path -LiteralPath $backupDir)) {
    New-Item -ItemType Directory -Path $backupDir | Out-Null
}

# 2. Backup current official EXE (only when version differs from target and not yet backed up)
$csproj = Join-Path $PSScriptRoot 'NeteaseToolbox.csproj'
[xml]$csprojXml = Get-Content -LiteralPath $csproj -Encoding UTF8
$targetVer = ($csprojXml.Project.PropertyGroup.FileVersion | Select-Object -First 1)
if (Test-Path -LiteralPath $output) {
    $currentVerInfo = (Get-Item -LiteralPath $output).VersionInfo
    $currentVer = $currentVerInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($currentVer)) { $currentVer = "2.0.0.0" }
    $existingSameVer = Get-ChildItem -LiteralPath $backupDir -Filter "网易云工具箱-v$currentVer-*.exe" -ErrorAction SilentlyContinue
    if ($currentVer -ne $targetVer -and -not $existingSameVer) {
        $timestamp = (Get-Date).ToString("yyyyMMdd-HHmmss")
        $backupFile = Join-Path $backupDir "网易云工具箱-v$currentVer-$timestamp.exe"
        Copy-Item -LiteralPath $output -Destination $backupFile -Force
        Write-Output "Backed up official EXE to: $backupFile"
    }
}

# 3. Compile WPF + Fluent project with dotnet build
Write-Output "Building NeteaseToolbox (WPF Fluent Architecture)..."
& dotnet build $csproj -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with code $LASTEXITCODE" }

# 4. Copy output executable and companion DLLs
$buildOutDir = Join-Path $PSScriptRoot 'bin\x64\Release'
$builtExe = Join-Path $buildOutDir '网易云工具箱.exe'
if (-not (Test-Path -LiteralPath $builtExe)) { throw "Build artifact not found: $builtExe" }

Stop-Process -Name "网易云工具箱" -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath $builtExe -Destination $output -Force
$configSrc = Join-Path $buildOutDir '网易云工具箱.exe.config'
if (Test-Path -LiteralPath $configSrc) {
    Copy-Item -LiteralPath $configSrc -Destination (Join-Path $PSScriptRoot '网易云工具箱.exe.config') -Force
}
@('ModernWpf.dll', 'ModernWpf.Controls.dll', 'System.ValueTuple.dll') | ForEach-Object {
    $dep = Join-Path $buildOutDir $_
    if (Test-Path -LiteralPath $dep) {
        Copy-Item -LiteralPath $dep -Destination (Join-Path $PSScriptRoot $_) -Force
    }
}

# 4b. Deploy convert.ps1 from the repo root so a toolbox-folder or direct bin run can convert songs
$repoRoot = Split-Path -Parent $PSScriptRoot
$convertScript = Join-Path $repoRoot 'convert.ps1'
if (-not (Test-Path -LiteralPath $convertScript)) { throw "Conversion script not found: $convertScript" }
Copy-Item -LiteralPath $convertScript -Destination (Join-Path $PSScriptRoot 'convert.ps1') -Force
Copy-Item -LiteralPath $convertScript -Destination (Join-Path $buildOutDir 'convert.ps1') -Force
Write-Output "Deployed convert.ps1 to toolbox and build output"

# 4c. Deploy the music-profile support folder used by portable_report.py
$musicProfileSource = Join-Path $repoRoot 'music-profile\refresh_listening_report.py'
if (-not (Test-Path -LiteralPath $musicProfileSource)) { throw "Report support script not found: $musicProfileSource" }
@((Join-Path $PSScriptRoot 'music-profile'), (Join-Path $buildOutDir 'music-profile')) | ForEach-Object {
    New-Item -ItemType Directory -Path $_ -Force | Out-Null
    Copy-Item -LiteralPath $musicProfileSource -Destination $_ -Force
}
Write-Output "Deployed music-profile support"

# 4d. Sandbox builds inherit Low integrity; raise local executables to Medium so %TEMP%,
#     %LOCALAPPDATA% and WebView2 work when the toolbox is launched from the workspace.
$mediumTargets = @(
    $output,
    (Join-Path $buildOutDir '网易云工具箱.exe'),
    (Join-Path $PSScriptRoot 'runtime\python\python.exe'),
    (Join-Path $PSScriptRoot 'runtime\node\node.exe'),
    (Join-Path $PSScriptRoot 'ffmpeg.exe')
) | Select-Object -Unique
foreach ($target in $mediumTargets) {
    if (-not (Test-Path -LiteralPath $target)) { continue }
    & icacls $target /setintegritylevel Medium | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Output "Warning: failed to set Medium integrity on $target" }
}
Write-Output "Ensured Medium integrity for local executables"

# 5. Clean older backups: strictly retain only the 2 most recent backups
$allBackups = Get-ChildItem -LiteralPath $backupDir -Filter "网易云工具箱-*.exe" | Sort-Object LastWriteTime -Descending
if ($allBackups.Count -gt 2) {
    $toDelete = $allBackups | Select-Object -Skip 2
    foreach ($item in $toDelete) {
        Remove-Item -LiteralPath $item.FullName -Force
        Write-Output "Removed older backup: $($item.Name)"
    }
}

# 6. Verify release version
$newVerInfo = (Get-Item -LiteralPath $output).VersionInfo
Write-Output "========================================"
Write-Output "Release build succeeded!"
Write-Output "File: $output"
Write-Output "ProductVersion: $($newVerInfo.ProductVersion)"
Write-Output "FileVersion: $($newVerInfo.FileVersion)"
Write-Output "========================================"
