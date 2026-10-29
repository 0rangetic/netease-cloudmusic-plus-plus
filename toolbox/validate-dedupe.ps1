[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$convert = Join-Path $project 'convert.ps1'

$script = Get-Content -LiteralPath $convert -Raw -Encoding UTF8
foreach ($forbidden in @('Remove-LowerQualityDuplicates', 'Remove-ConvertedFilesForSource', 'Remove-Item -LiteralPath $candidate', 'Remove-Item -LiteralPath $loser')) {
    if ($script.Contains($forbidden)) {
        throw "convert.ps1 still contains a media deletion path: $forbidden"
    }
}
foreach ($line in ($script -split "`r?`n")) {
    if ($line -notmatch 'Remove-Item') { continue }
    if ($line -match '\$(temporary|tagged)') { continue }
    throw "convert.ps1 has an unreviewed Remove-Item call: $line"
}
if ($script -notmatch '旧去重入口已停用') {
    throw 'convert.ps1 does not guard the legacy deduplicate entry.'
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework C# compiler is unavailable.' }
$tempRoot = [IO.Path]::GetTempPath()
$sandbox = Join-Path $tempRoot ('ncm-dedupe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
try {
    $exe = Join-Path $sandbox 'DedupeValidation.exe'
    $arguments = @('/nologo', '/target:exe', '/platform:x64', '/utf8output', '/codepage:65001',
        '/reference:System.Core.dll', '/reference:System.Web.Extensions.dll', "/out:$exe",
        (Join-Path $PSScriptRoot 'DedupeValidation.cs'),
        (Join-Path $PSScriptRoot 'DedupeModels.cs'),
        (Join-Path $PSScriptRoot 'DedupePlanner.cs'),
        (Join-Path $PSScriptRoot 'DedupeQuarantine.cs'))
    & $compiler $arguments
    if ($LASTEXITCODE -ne 0) { throw 'Dedupe validation compilation failed.' }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw 'Dedupe validation failed.' }
    Write-Output 'Dedupe safety audit and planner validation passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($sandbox)
    if ($resolved.StartsWith([IO.Path]::GetFullPath($tempRoot), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'ncm-dedupe-*') {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
