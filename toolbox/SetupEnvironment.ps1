[CmdletBinding()]
param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
if (-not [Environment]::Is64BitOperatingSystem) { throw '需要 Windows 10/11 64 位。' }
$release = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
if (-not $release -or $release -lt 528040) { throw '请先安装 Microsoft .NET Framework 4.8：https://dotnet.microsoft.com/download/dotnet-framework/net48' }
$python = Join-Path $PSScriptRoot 'runtime\python\python.exe'
if (-not (Test-Path -LiteralPath $python)) { throw '缺少内置 Python，请完整解压便携包。' }
& $python -X utf8 -c 'import ssl,sqlite3,ctypes;from Crypto.Cipher import AES;import qrcode,frida,psutil'
if ($LASTEXITCODE) { throw '内置 Python 组件检查失败。' }
Write-Output 'Python 组件可用。'
$node = Join-Path $PSScriptRoot 'runtime\node\node.exe'
& $node --version
if ($LASTEXITCODE) { throw '内置 Node.js 检查失败。' }
$webview = $false
foreach ($key in @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}')) {
    $version = (Get-ItemProperty $key -ErrorAction SilentlyContinue).pv
    if ($version -and [version]$version -gt [version]'0.0.0.0') { $webview = $true }
}
if ($webview) { Write-Output 'WebView2 已安装。' }
elseif ($CheckOnly) { throw 'WebView2 未安装，请运行“配置运行环境.cmd”。' }
else {
    $installer = Join-Path $PSScriptRoot 'installers\MicrosoftEdgeWebview2Setup.exe'
    $signature = Get-AuthenticodeSignature -LiteralPath $installer
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'WebView2 安装程序的微软签名验证失败。' }
    Write-Output '正在联网安装微软 WebView2，可能需要几分钟…'
    $process = Start-Process -FilePath $installer -ArgumentList '/silent','/install' -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw ('WebView2 安装失败，退出码：' + $process.ExitCode) }
    Write-Output 'WebView2 安装完成。'
}
Write-Output '运行环境已就绪。选择音乐目录并登录网易云后即可使用；AI 服务和三行歌词按需配置。'
