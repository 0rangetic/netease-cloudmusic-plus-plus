# 音频转麦克风页面布局校验。
# 校验程序必须从工作区之外的目录运行：工作区里的可执行文件在 Windows 上只能读到
# 部分进程，「网易云进程」会被误判为未运行。NAudio 也要放在校验程序旁边，
# 因为工具箱 EXE 是按字节加载的，依赖从校验程序所在目录解析。
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $PSScriptRoot 'AudioForwardingLayoutValidation.exe'
& $compiler /nologo /target:exe /platform:x64 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll "/out:$output" "$PSScriptRoot\AudioForwardingLayoutValidation.cs"
if ($LASTEXITCODE -ne 0) { throw 'Audio layout validation compilation failed.' }
$tempRoot = [IO.Path]::GetTempPath()
$sandbox = Join-Path $tempRoot ('ncm-audio-layout-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
try {
    Copy-Item -LiteralPath $output, (Join-Path $PSScriptRoot 'NAudio.Core.dll'), (Join-Path $PSScriptRoot 'NAudio.Wasapi.dll') -Destination $sandbox
    $screenshots = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\audio-page-layout'
    & (Join-Path $sandbox 'AudioForwardingLayoutValidation.exe') (Join-Path $PSScriptRoot '网易云工具箱.exe') $screenshots
    if ($LASTEXITCODE -ne 0) { throw 'Audio layout validation failed.' }
    Write-Output $screenshots
} finally {
    # 只清理本次创建的那个临时目录，并先确认路径确实在临时目录下。
    if ($sandbox.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $sandbox -Leaf) -like 'ncm-audio-layout-*') {
        Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}