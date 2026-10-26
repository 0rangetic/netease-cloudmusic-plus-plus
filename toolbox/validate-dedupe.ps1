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
foreach ($required in @('function Save-ConversionLedger', 'ConversionTaskId', 'SourceSha256', 'Outputs = @($recordOutputs)')) {
    if (-not $script.Contains($required)) {
        throw "convert.ps1 is missing conversion provenance support: $required"
    }
}
$toolboxSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Toolbox.cs') -Raw -Encoding UTF8
foreach ($forbidden in @('-DeduplicateOnly', 'Remove-LowerQualityDuplicates', 'Remove-ConvertedFilesForSource')) {
    if ($toolboxSource.Contains($forbidden)) { throw "Toolbox.cs still contains a legacy destructive dedupe path: $forbidden" }
}
foreach ($required in @('DedupePreviewForm', 'DedupeTrashForm', 'DetectConversionDuplicates', '-ConversionTaskId', 'OpenSafeDedupe')) {
    if (-not $toolboxSource.Contains($required)) { throw "Toolbox.cs is missing safe dedupe integration: $required" }
}
function New-TestNcm([string]$Path, [string]$SongId, [string]$Title, [string]$Artist, [string]$Format, [long]$Bitrate) {
    $metaKey = [byte[]](0x23,0x31,0x34,0x6C,0x6A,0x6B,0x5F,0x21,0x5C,0x5D,0x26,0x30,0x55,0x3C,0x27,0x28)
    $json = '{"musicId":' + $SongId + ',"musicName":"' + $Title.Replace('\','\\').Replace('"','\"') + '","artist":[["' + $Artist.Replace('\','\\').Replace('"','\"') + '"]],"format":"' + $Format + '","bitrate":' + $Bitrate + '}'
    $plain = [Text.Encoding]::UTF8.GetBytes('music:' + $json)
    $aes = [Security.Cryptography.Aes]::Create()
    try {
        $aes.Mode = [Security.Cryptography.CipherMode]::ECB
        $aes.Padding = [Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = $metaKey
        $encryptor = $aes.CreateEncryptor()
        try { $encrypted = $encryptor.TransformFinalBlock($plain, 0, $plain.Length) }
        finally { $encryptor.Dispose() }
    }
    finally { $aes.Dispose() }
    $prefix = [Text.Encoding]::ASCII.GetBytes("163 key(Don't modify):")
    $encoded = [Text.Encoding]::ASCII.GetBytes([Convert]::ToBase64String($encrypted))
    $meta = New-Object byte[] ($prefix.Length + $encoded.Length)
    [Array]::Copy($prefix, 0, $meta, 0, $prefix.Length)
    [Array]::Copy($encoded, 0, $meta, $prefix.Length, $encoded.Length)
    for ($i = 0; $i -lt $meta.Length; $i++) { $meta[$i] = $meta[$i] -bxor 0x63 }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    $stream = [IO.File]::Create($Path)
    $writer = New-Object IO.BinaryWriter($stream)
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('CTENFDAM'))
        $writer.Write([byte[]](0, 0))
        $writer.Write([uint32]1)
        $writer.Write([byte]0)
        $writer.Write([uint32]$meta.Length)
        $writer.Write($meta)
        $writer.Write([byte[]](0, 0, 0, 0, 0, 0, 0, 0, 0))
        $writer.Write([uint32]0)
        $writer.Write([Text.Encoding]::UTF8.GetBytes('audio'))
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework C# compiler is unavailable.' }
$tempRoot = [IO.Path]::GetTempPath()
$sandbox = Join-Path $tempRoot ('ncm-dedupe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
try {
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($convert), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw 'convert.ps1 has PowerShell syntax errors.' }
    $wanted = @('Get-FileSha256', 'Get-ConversionOutputKind', 'New-ConversionOutputRecord', 'Save-ConversionLedger')
    $functions = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $wanted -contains $node.Name
    }, $true))
    if ($functions.Count -ne $wanted.Count) { throw 'convert.ps1 provenance functions are incomplete.' }
    Invoke-Expression (($functions | ForEach-Object { $_.Extent.Text }) -join "`r`n")
    $previousLocalAppData = $env:LOCALAPPDATA
    try {
        $env:LOCALAPPDATA = Join-Path $sandbox 'provenance-local'
        $sample = Join-Path $sandbox 'provenance-sample.mp3'
        [IO.File]::WriteAllText($sample, 'audio', (New-Object Text.UTF8Encoding($false)))
        $outputRecord = New-ConversionOutputRecord $sample
        $record = [pscustomobject]@{
            Version = 1
            TaskId = 'provenance-test'
            SourcePath = $sample
            SourceSongId = '1'
            SourceSize = (Get-Item -LiteralPath $sample).Length
            SourceModifiedUtc = (Get-Item -LiteralPath $sample).LastWriteTimeUtc.ToString('o')
            SourceSha256 = (Get-FileSha256 $sample)
            Success = $true
            Error = ''
            ConvertedUtc = (Get-Date).ToUniversalTime().ToString('o')
            Outputs = @($outputRecord)
        }
        Save-ConversionLedger 'provenance-test' (Get-Date).ToUniversalTime().ToString('o') @($record)
        $ledgerPath = Join-Path $env:LOCALAPPDATA 'NeteaseToolbox\Dedupe\conversions\provenance-test.json'
        if (-not (Test-Path -LiteralPath $ledgerPath -PathType Leaf)) { throw 'Conversion provenance ledger was not written.' }
        $ledger = Get-Content -LiteralPath $ledgerPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $validLedger = $ledger.Records.Count -eq 1 -and $ledger.Records[0].Outputs.Count -eq 1 -and $ledger.Records[0].Outputs[0].Kind -eq 'Audio' -and ([string]$ledger.Records[0].SourceSha256).Length -eq 64
        if (-not $validLedger) { throw 'Conversion provenance ledger content is incomplete.' }
        Write-Output 'Provenance ledger write test passed.'
    }
    finally {
        $env:LOCALAPPDATA = $previousLocalAppData
    }
    $scanRoot = Join-Path $sandbox 'scan-library'
    New-TestNcm (Join-Path $scanRoot 'song_HQ.ncm') '123' '测试歌曲' '测试歌手' 'flac' 900000
    New-TestNcm (Join-Path $scanRoot 'song_LQ.ncm') '123' '测试歌曲' '测试歌手' 'mp3' 320000
    New-TestNcm (Join-Path $scanRoot 'other.ncm') '456' '另一首歌' '测试歌手' 'mp3' 320000
    $beforeScan = @{}
    foreach ($file in Get-ChildItem -LiteralPath $scanRoot -File -Recurse) {
        $beforeScan[$file.FullName] = @{
            Hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            Ticks = $file.LastWriteTimeUtc.Ticks
        }
    }
    $scanJson = Join-Path $sandbox 'scan.json'
    & $convert -ScanOnly -ScanJsonPath $scanJson -SourceRoot $scanRoot -Recurse
    if ($LASTEXITCODE -notin @(0, $null)) { throw 'Real NCM scan returned a non-zero exit code.' }
    foreach ($file in Get-ChildItem -LiteralPath $scanRoot -File -Recurse) {
        $before = $beforeScan[$file.FullName]
        if (-not $before) { throw "Scan created an unexpected file: $($file.FullName)" }
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $before.Hash -or $file.LastWriteTimeUtc.Ticks -ne $before.Ticks) {
            throw "Scan modified source file: $($file.FullName)"
        }
    }
    $scanResult = Get-Content -LiteralPath $scanJson -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($scanResult.Files.Count -ne 3) { throw 'Real NCM scan did not return all test files.' }
    foreach ($file in $scanResult.Files) {
        if (-not $file.MetadataOk) { throw "Real NCM metadata could not be read: $($file.Path) $($file.Error)" }
    }
    $sameSong = @($scanResult.Files | Where-Object { [string]$_.SongId -eq '123' })
    if ($sameSong.Count -ne 2) { throw 'Real NCM scan did not preserve duplicate song IDs.' }
    if (@($sameSong | Where-Object { $_.Format -eq 'flac' }).Count -ne 1) { throw 'Real NCM scan lost format metadata.' }
    Write-Output 'Real NCM read-only scan test passed.'
    & $convert -DeduplicateOnly -SourceRoot $scanRoot
    if ($LASTEXITCODE -notin @(0, $null)) { throw 'Legacy deduplicate entry returned a non-zero exit code.' }
    foreach ($file in Get-ChildItem -LiteralPath $scanRoot -File -Recurse) {
        $before = $beforeScan[$file.FullName]
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $before.Hash) {
            throw "Legacy deduplicate entry modified a source file: $($file.FullName)"
        }
    }
    Write-Output 'Legacy deduplicate entry is read-only.'
    $exe = Join-Path $sandbox 'DedupeValidation.exe'
    $arguments = @('/nologo', '/target:exe', '/platform:x64', '/utf8output', '/codepage:65001',
        '/reference:System.Core.dll', '/reference:System.Web.Extensions.dll',
        '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', "/out:$exe",
        (Join-Path $PSScriptRoot 'DedupeValidation.cs'),
        (Join-Path $PSScriptRoot 'DedupeModels.cs'),
        (Join-Path $PSScriptRoot 'DedupePlanner.cs'),
        (Join-Path $PSScriptRoot 'DedupeQuarantine.cs'),
        (Join-Path $PSScriptRoot 'DedupeUi.cs'))
    & $compiler $arguments
    if ($LASTEXITCODE -ne 0) { throw 'Dedupe validation compilation failed.' }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw 'Dedupe validation failed.' }
    & $exe --scan-json $scanJson
    if ($LASTEXITCODE -ne 0) { throw 'Dedupe planner did not consume the real scan JSON correctly.' }
    Write-Output 'Dedupe safety audit and planner validation passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($sandbox)
    if ($resolved.StartsWith([IO.Path]::GetFullPath($tempRoot), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'ncm-dedupe-*') {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
