[CmdletBinding()]
param([string]$ToolboxExe)
if (-not $ToolboxExe) { $ToolboxExe = Join-Path $PSScriptRoot '网易云工具箱.exe' }
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework C# compiler is unavailable.' }
$tempRoot = [IO.Path]::GetTempPath()
$sandbox = Join-Path $tempRoot ('ncm-tag-playlist-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
try {
    $logicExe = Join-Path $sandbox 'TagPlaylistFilterValidation.exe'
    $logicArgs = @('/nologo', '/target:exe', '/platform:x64', '/utf8output', '/codepage:65001',
        '/reference:System.Core.dll', "/out:$logicExe",
        (Join-Path $PSScriptRoot 'TagPlaylistFilterValidation.cs'),
        (Join-Path $PSScriptRoot 'TagPlaylistFilter.cs'))
    & $compiler $logicArgs
    if ($LASTEXITCODE -ne 0) { throw 'Tag playlist filter validation compilation failed.' }
    & $logicExe
    if ($LASTEXITCODE -ne 0) { throw 'Tag playlist filter validation failed.' }

    $ruleExe = Join-Path $sandbox 'PlaylistRuleStoreValidation.exe'
    $ruleArgs = @('/nologo', '/target:exe', '/platform:x64', '/utf8output', '/codepage:65001',
        '/reference:System.Core.dll', '/reference:System.Web.Extensions.dll', "/out:$ruleExe",
        (Join-Path $PSScriptRoot 'PlaylistRuleStoreValidation.cs'),
        (Join-Path $PSScriptRoot 'PlaylistRuleStore.cs'))
    & $compiler $ruleArgs
    if ($LASTEXITCODE -ne 0) { throw 'Playlist rule store validation compilation failed.' }
    & $ruleExe
    if ($LASTEXITCODE -ne 0) { throw 'Playlist rule store validation failed.' }

    $layoutExe = Join-Path $sandbox 'TagPlaylistLayoutValidation.exe'
    $layoutArgs = @('/nologo', '/target:exe', '/platform:x64', '/utf8output', '/codepage:65001',
        '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Core.dll',
        "/out:$layoutExe", (Join-Path $PSScriptRoot 'TagPlaylistLayoutValidation.cs'))
    & $compiler $layoutArgs
    if ($LASTEXITCODE -ne 0) { throw 'Tag playlist layout validation compilation failed.' }

    $editorExe = Join-Path $sandbox 'PlaylistEditorLayoutValidation.exe'
    $editorArgs = @('/nologo', '/target:exe', '/platform:x64', '/utf8output', '/codepage:65001',
        '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Core.dll',
        "/out:$editorExe", (Join-Path $PSScriptRoot 'PlaylistEditorLayoutValidation.cs'))
    & $compiler $editorArgs
    if ($LASTEXITCODE -ne 0) { throw 'Playlist editor layout validation compilation failed.' }

    Copy-Item -LiteralPath ([IO.Path]::GetFullPath($ToolboxExe)) -Destination $sandbox
    foreach ($name in @('NAudio.Core.dll', 'NAudio.Wasapi.dll', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll', 'genre_analysis.py')) {
        $source = Join-Path $PSScriptRoot $name
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $sandbox }
    }
    & $layoutExe (Join-Path $sandbox (Split-Path $ToolboxExe -Leaf))
    if ($LASTEXITCODE -ne 0) { throw 'Tag playlist layout validation failed.' }
    & $editorExe (Join-Path $sandbox (Split-Path $ToolboxExe -Leaf))
    if ($LASTEXITCODE -ne 0) { throw 'Playlist editor layout validation failed.' }
    Write-Output 'Tag playlist validation passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($sandbox)
    if ($resolved.StartsWith([IO.Path]::GetFullPath($tempRoot), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'ncm-tag-playlist-*') {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
