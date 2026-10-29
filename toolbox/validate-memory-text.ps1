param(
    [string]$ToolboxExe = (Join-Path $PSScriptRoot '网易云工具箱.exe'),
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\memory-text-validation\fixed')
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$tempRoot = [IO.Path]::GetTempPath()
$sandbox = Join-Path $tempRoot ('ncm-memory-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
$testExe = Join-Path $sandbox 'MemoryTextRenderingValidation.exe'
try {
    & $compiler /nologo /target:exe /platform:x64 /utf8output /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll "/win32manifest:$PSScriptRoot\app.manifest" "/out:$testExe" "$PSScriptRoot\MemoryTextRenderingValidation.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Memory rendering test compilation failed.' }
    foreach ($name in @('NAudio.Core.dll', 'NAudio.Wasapi.dll', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $sandbox
    }
    & $testExe ([IO.Path]::GetFullPath($ToolboxExe)) ([IO.Path]::GetFullPath($OutputDirectory))
    if ($LASTEXITCODE -ne 0) { throw 'Memory rendering validation failed.' }
} finally {
    $resolvedSandbox = [IO.Path]::GetFullPath($sandbox)
    if ($resolvedSandbox.StartsWith([IO.Path]::GetFullPath($tempRoot), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolvedSandbox -Leaf) -like 'ncm-memory-test-*') {
        Remove-Item -LiteralPath $resolvedSandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}
