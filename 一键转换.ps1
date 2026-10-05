$ErrorActionPreference = 'Stop'
$mainScript = Join-Path $PSScriptRoot 'convert.ps1'
if (-not (Test-Path -LiteralPath $mainScript -PathType Leaf)) {
    throw "找不到主转换脚本：$mainScript"
}

$outputDir = $null
$filePaths = @()
for ($i = 0; $i -lt $args.Count; $i++) {
    if ($args[$i] -in @('-OutputDir', '-OutputDirectory') -and ($i + 1) -lt $args.Count) {
        $i++
        $outputDir = [string]$args[$i]
    } else {
        $filePaths += [string]$args[$i]
    }
}

$previousOutputDir = $env:NCM_OUTPUT_DIR
try {
    if ($outputDir) { $env:NCM_OUTPUT_DIR = $outputDir }
    & $mainScript @filePaths
} finally {
    if ($null -eq $previousOutputDir) { Remove-Item Env:\NCM_OUTPUT_DIR -ErrorAction SilentlyContinue }
    else { $env:NCM_OUTPUT_DIR = $previousOutputDir }
}