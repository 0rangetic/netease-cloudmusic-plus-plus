[CmdletBinding()]
param([string]$Destination)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
if (-not $Destination) { $Destination = Join-Path $project ('dist\网易云工具箱便携包-' + (Get-Item (Join-Path $PSScriptRoot '网易云工具箱.exe')).VersionInfo.FileVersion) }
$Destination = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $Destination) { throw '目标目录已存在，请使用新的空目录，避免混入旧配置或个人数据。' }
New-Item -ItemType Directory -Path $Destination | Out-Null
$files = @('网易云工具箱.exe', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll',
    'WebView2Loader.dll', 'netease_auth.py', 'portable_paths.py', 'portable_report.py', 'genre_analysis.py',
    'library_analysis.py', 'memory_recall.py', 'portrait_ai.py', 'profile_analysis.py', 'profile_fetch.py',
    'profile_first_listen.js', 'SetupEnvironment.ps1', '配置运行环境.cmd')
foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $Destination }
Copy-Item -LiteralPath (Join-Path $project 'convert.ps1'), (Join-Path $project '一键转换.ps1') -Destination $Destination
Copy-Item -LiteralPath (Join-Path $project 'music-profile\refresh_listening_report.py') -Destination $Destination
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'portable-README.txt') -Destination (Join-Path $Destination '使用说明.txt')
foreach ($folder in @('runtime\python', 'runtime\node', 'installers', 'Lyric3', 'vendor')) {
    $source = Join-Path $PSScriptRoot $folder
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        if ($file.FullName -match '[\\/]__pycache__[\\/]' -or $file.Extension -in '.pyc','.log') { continue }
        $relative = $file.FullName.Substring($PSScriptRoot.Length).TrimStart('\')
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
$ffmpeg = Join-Path $PSScriptRoot 'ffmpeg.exe'
if (-not (Test-Path -LiteralPath $ffmpeg)) { throw '构建便携包前，请将 FFmpeg 和其许可证放在 toolbox 目录。' }
Copy-Item -LiteralPath $ffmpeg -Destination $Destination
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ffmpeg-LICENSE.txt') -Destination $Destination
Get-ChildItem -LiteralPath $Destination -File -Recurse | Unblock-File
$manifest = foreach ($file in Get-ChildItem -LiteralPath $Destination -File -Recurse) {
    [ordered]@{ path=$file.FullName.Substring($Destination.Length).TrimStart('\'); bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
[ordered]@{ version=(Get-Item (Join-Path $Destination '网易云工具箱.exe')).VersionInfo.FileVersion; files=@($manifest) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Destination 'manifest.json') -Encoding UTF8
Write-Output $Destination
