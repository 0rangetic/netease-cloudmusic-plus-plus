# 多人一起听页面布局校验。
# 校验程序必须从工作区之外的目录运行：工作区里的可执行文件在 Windows 上只能读到
# 部分进程，「网易云进程」会被误判为未运行。依赖 DLL 也要放在校验程序旁边，
# 因为工具箱 EXE 是按字节加载的，依赖从校验程序所在目录解析。
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$tempRoot = [IO.Path]::GetTempPath()
$sandbox = Join-Path $tempRoot ('ncm-multi-listen-layout-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
$testExe = Join-Path $sandbox 'MultiListenLayoutValidation.exe'
try {
    & $compiler /nologo /target:exe /platform:x64 /utf8output /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll "/out:$testExe" "$PSScriptRoot\MultiListenLayoutValidation.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Multi listen layout validation compilation failed.' }
    $stage = @(
        'NAudio.Core.dll',
        'NAudio.Wasapi.dll',
        'Microsoft.Web.WebView2.Core.dll',
        'Microsoft.Web.WebView2.WinForms.dll',
        'WebView2Loader.dll'
    )
    foreach ($dll in $stage) {
        $source = Join-Path $PSScriptRoot $dll
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination $sandbox
        }
    }
    $screenshots = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\multi-listen-page-layout'
    $toolboxExe = Join-Path $PSScriptRoot '网易云工具箱.exe'
    & $testExe $toolboxExe $screenshots
    if ($LASTEXITCODE -ne 0) { throw 'Multi listen layout validation failed.' }
    Write-Output $screenshots
} finally {
    # 只清理本次创建的那个临时目录，并先确认路径确实在临时目录下。
    if ($sandbox.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $sandbox -Leaf) -like 'ncm-multi-listen-layout-*') {
        Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}
