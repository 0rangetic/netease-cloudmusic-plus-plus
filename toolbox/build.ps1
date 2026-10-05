$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework C# compiler is unavailable.' }
$source = Join-Path $PSScriptRoot 'Toolbox.cs'
$themeSource = Join-Path $PSScriptRoot 'Theme.cs'
$appearanceSource = Join-Path $PSScriptRoot 'Appearance.cs'
$wallpaperListSource = Join-Path $PSScriptRoot 'WallpaperListView.cs'
$cachedTableSource = Join-Path $PSScriptRoot 'CachedSongTable.cs'
$wallpaperTextSource = Join-Path $PSScriptRoot 'WallpaperRichTextBox.cs'
$playbackSource = Join-Path $PSScriptRoot 'PlaybackRecorder.cs'
$authSource = Join-Path $PSScriptRoot 'NeteaseAuth.cs'
$loginSource = Join-Path $PSScriptRoot 'LoginUi.cs'
$portableSource = Join-Path $PSScriptRoot 'PortableEnvironment.cs'
$officialLoginSource = Join-Path $PSScriptRoot 'OfficialLogin.cs'
$webviewCore = Join-Path $PSScriptRoot 'Microsoft.Web.WebView2.Core.dll'
$webviewForms = Join-Path $PSScriptRoot 'Microsoft.Web.WebView2.WinForms.dll'
if (-not (Test-Path -LiteralPath $webviewCore) -or -not (Test-Path -LiteralPath $webviewForms) -or -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'WebView2Loader.dll'))) { & (Join-Path $PSScriptRoot 'prepare-webview2.ps1') }
$navigationSource = Join-Path $PSScriptRoot 'SongNavigation.cs'
$manifest = Join-Path $PSScriptRoot 'app.manifest'
$output = Join-Path $PSScriptRoot '网易云工具箱.exe'
$winMetadata = Join-Path $env:WINDIR 'System32\WinMetadata'
$windowsRuntime = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL') -Recurse -File -Filter 'System.Runtime.WindowsRuntime.dll' | Select-Object -First 1 -ExpandProperty FullName
if (-not $windowsRuntime) { throw 'System.Runtime.WindowsRuntime.dll is unavailable.' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.Core.dll /reference:System.Security.dll /reference:System.Web.Extensions.dll /reference:System.Runtime.dll "/reference:$webviewCore" "/reference:$webviewForms" "/reference:$windowsRuntime" "/reference:$winMetadata\Windows.Foundation.winmd" "/reference:$winMetadata\Windows.Media.winmd" "/win32manifest:$manifest" "/out:$output" $source $themeSource $appearanceSource $wallpaperListSource $cachedTableSource $wallpaperTextSource $playbackSource $navigationSource $authSource $loginSource $officialLoginSource $portableSource
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with code $LASTEXITCODE" }
Write-Output $output
