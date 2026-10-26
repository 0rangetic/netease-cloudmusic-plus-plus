# NCM decoding algorithm adapted from Majjcom/ncmpp (MIT License).
# Copyright (c) 2023 Majjcom
# Permission is hereby granted, free of charge, to any person obtaining a copy
# of this software and associated documentation files (the "Software"), to deal
# in the Software without restriction, including without limitation the rights
# to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
# copies of the Software, and to permit persons to whom the Software is
# furnished to do so, subject to the following conditions:
# The above copyright notice and this permission notice shall be included in all
# copies or substantial portions of the Software.
# THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
# IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
# FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.

[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$OutputDir,
    [string]$SourceRoot,
    [switch]$DeduplicateOnly,
    [switch]$ScanOnly,
    [string]$ScanJsonPath,
    [switch]$Recurse,
    [string]$ConversionTaskId,
    [switch]$ExtractCover,
    [string]$CoverOutputDir,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Files
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

function Select-NcmInputFiles {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    $form = New-Object System.Windows.Forms.Form
    $form.Text = '选择要转换的歌曲'
    $form.ClientSize = New-Object System.Drawing.Size(460, 150)
    $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.ShowInTaskbar = $true

    $label = New-Object System.Windows.Forms.Label
    $label.AutoSize = $true
    $label.Location = New-Object System.Drawing.Point(24, 22)
    $label.Text = '选择部分文件，或一次选中一个文件夹内的全部 NCM 歌曲。'
    $form.Controls.Add($label)

    $chooseButton = New-Object System.Windows.Forms.Button
    $chooseButton.Text = '选择文件…'
    $chooseButton.Size = New-Object System.Drawing.Size(120, 38)
    $chooseButton.Location = New-Object System.Drawing.Point(24, 72)
    $chooseButton.Add_Click({
        $dialog = New-Object System.Windows.Forms.OpenFileDialog
        try {
            $dialog.Title = '选择要转换的网易云 NCM 歌曲（可多选，Ctrl+A 全选）'
            $dialog.Filter = '网易云歌曲 (*.ncm)|*.ncm'
            $dialog.Multiselect = $true
            if ($dialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
                $form.Tag = @($dialog.FileNames)
                $form.DialogResult = [System.Windows.Forms.DialogResult]::OK
                $form.Close()
            }
        } finally {
            $dialog.Dispose()
        }
    })
    $form.Controls.Add($chooseButton)

    $selectAllButton = New-Object System.Windows.Forms.Button
    $selectAllButton.Text = '全选文件夹…'
    $selectAllButton.Size = New-Object System.Drawing.Size(132, 38)
    $selectAllButton.Location = New-Object System.Drawing.Point(158, 72)
    $selectAllButton.Add_Click({
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        try {
            $dialog.Description = '选择文件夹；其中及子文件夹内的所有 NCM 歌曲都会被选中'
            $dialog.ShowNewFolderButton = $false
            if ($dialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
                $selectedFiles = @((Get-SafeNcmFiles $dialog.SelectedPath $true).Files)
                if ($selectedFiles.Count -eq 0) {
                    [void][System.Windows.Forms.MessageBox]::Show($form, '该文件夹内没有 NCM 歌曲。', '没有可选文件', 'OK', 'Information')
                    return
                }
                $form.Tag = $selectedFiles
                $form.DialogResult = [System.Windows.Forms.DialogResult]::OK
                $form.Close()
            }
        } finally {
            $dialog.Dispose()
        }
    })
    $form.Controls.Add($selectAllButton)

    $cancelButton = New-Object System.Windows.Forms.Button
    $cancelButton.Text = '取消'
    $cancelButton.Size = New-Object System.Drawing.Size(100, 38)
    $cancelButton.Location = New-Object System.Drawing.Point(336, 72)
    $cancelButton.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.CancelButton = $cancelButton
    $form.Controls.Add($cancelButton)

    try {
        if ($form.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return @() }
        return @($form.Tag)
    } finally {
        $form.Dispose()
    }
}

$decoderSource = @'
using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;

public sealed class NcmResult {
    public string AudioPath;
    public string MetadataJson;
    public byte[] CoverData;
}

public static class NcmDecoder {
    static readonly byte[] CoreKey = Hex("687A4852416D736F356B496E62617857");
    static readonly byte[] MetaKey = Hex("2331346C6A6B5F215C5D2630553C2728");
    static byte[] Hex(string s) { byte[] b=new byte[s.Length/2]; for(int i=0;i<b.Length;i++) b[i]=Convert.ToByte(s.Substring(i*2,2),16); return b; }
    static byte[] Decrypt(byte[] input, byte[] key) {
        using(Aes aes=Aes.Create()) { aes.Mode=CipherMode.ECB; aes.Padding=PaddingMode.None; aes.Key=key; using(ICryptoTransform t=aes.CreateDecryptor()) return t.TransformFinalBlock(input,0,input.Length); }
    }
    static byte[] Unpad(byte[] b) { if(b.Length==0) return b; int n=b[b.Length-1]; if(n<1||n>16||n>b.Length) throw new InvalidDataException("Invalid NCM padding"); byte[] r=new byte[b.Length-n]; Buffer.BlockCopy(b,0,r,0,r.Length); return r; }
    static uint ReadLength(BinaryReader r) { return r.ReadUInt32(); }
    public static NcmResult ReadCoverFile(string input, string outputDir) {
        using(FileStream fs=File.OpenRead(input)) using(BinaryReader br=new BinaryReader(fs)) {
            byte[] sig=br.ReadBytes(8); if(Encoding.ASCII.GetString(sig)!="CTENFDAM") throw new InvalidDataException("Not a NetEase NCM file");
            br.ReadBytes(2);
            uint kl=ReadLength(br); if(kl==0||kl>1048576) throw new InvalidDataException("Invalid NCM key block"); fs.Seek(kl,SeekOrigin.Current);
            uint ml=ReadLength(br); if(ml<22||ml>1048576) throw new InvalidDataException("Invalid NCM metadata block");
            byte[] mb=br.ReadBytes((int)ml); if(mb.Length!=(int)ml) throw new EndOfStreamException("Incomplete NCM metadata block");
            for(int i=0;i<mb.Length;i++) mb[i]^=0x63;
            string encoded=Encoding.ASCII.GetString(mb,22,mb.Length-22); byte[] cipher=System.Convert.FromBase64String(encoded);
            string json=Encoding.UTF8.GetString(Unpad(Decrypt(cipher,MetaKey)));
            if(json.Length<6) throw new InvalidDataException("Invalid NCM metadata");
            string metadata=json.Substring(6);
            fs.Seek(9,SeekOrigin.Current); uint cover=ReadLength(br); if(cover>67108864) throw new InvalidDataException("Invalid NCM cover block");
            byte[] coverData=br.ReadBytes((int)cover); if(coverData.Length!=(int)cover) throw new EndOfStreamException("Incomplete NCM cover block");
            string output=Path.Combine(outputDir,Path.GetFileNameWithoutExtension(input)+".flac");
            return new NcmResult{AudioPath=output,MetadataJson=metadata,CoverData=coverData};
        }
    }
    public static NcmResult ConvertFile(string input, string outputDir) {
        Directory.CreateDirectory(outputDir);
        using(FileStream fs=File.OpenRead(input)) using(BinaryReader br=new BinaryReader(fs)) {
            byte[] sig=br.ReadBytes(8); if(Encoding.ASCII.GetString(sig)!="CTENFDAM") throw new InvalidDataException("Not a NetEase NCM file");
            br.ReadBytes(2);
            uint kl=ReadLength(br); if(kl==0||kl>1048576) throw new InvalidDataException("Invalid NCM key block");
            byte[] kb=br.ReadBytes((int)kl); for(int i=0;i<kb.Length;i++) kb[i]^=0x64;
            byte[] key=Unpad(Decrypt(kb,CoreKey)); if(key.Length<17) throw new InvalidDataException("Invalid NCM music key");
            byte[] box=new byte[256]; for(int i=0;i<256;i++) box[i]=(byte)i;
            int last=0, off=0; for(int i=0;i<256;i++){ int swap=box[i]; int c=(swap+last+key[17+off])&255; off++; if(off>=key.Length-17) off=0; box[i]=box[c]; box[c]=(byte)swap; last=c; }
            uint ml=ReadLength(br); if(ml<22||ml>1048576) throw new InvalidDataException("Invalid NCM metadata block");
            byte[] mb=br.ReadBytes((int)ml); for(int i=0;i<mb.Length;i++) mb[i]^=0x63;
            string encoded=Encoding.ASCII.GetString(mb,22,mb.Length-22); byte[] cipher=System.Convert.FromBase64String(encoded);
            string json=Encoding.UTF8.GetString(Unpad(Decrypt(cipher,MetaKey)));
            if(json.Length<6) throw new InvalidDataException("Invalid NCM metadata");
            string metadata=json.Substring(6);
            fs.Seek(9,SeekOrigin.Current); uint cover=ReadLength(br); if(cover>67108864) throw new InvalidDataException("Invalid NCM cover block");
            byte[] coverData=br.ReadBytes((int)cover); if(coverData.Length!=(int)cover) throw new EndOfStreamException("Incomplete NCM cover block");
            System.Text.RegularExpressions.Match fm=System.Text.RegularExpressions.Regex.Match(metadata,"\\\"format\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"");
            string fmt=fm.Success?fm.Groups[1].Value:"flac"; if(fmt.StartsWith(".")) fmt=fmt.Substring(1); fmt=Path.GetExtension("x."+fmt).TrimStart('.');
            string outPath=Path.Combine(outputDir,Path.GetFileNameWithoutExtension(input)+"."+fmt);
            using(FileStream output=File.Create(outPath)) { byte[] buf=new byte[32768]; int n; long pos=0; while((n=fs.Read(buf,0,buf.Length))>0){ for(int i=0;i<n;i++){ int j=(int)((pos+i+1)&255); buf[i]^=box[(box[j]+box[(box[j]+j)&255])&255]; } output.Write(buf,0,n); pos+=n; } }
            return new NcmResult{AudioPath=outPath,MetadataJson=metadata,CoverData=coverData};
        }
    }
}
'@

Add-Type -TypeDefinition $decoderSource -Language CSharp
Add-Type -AssemblyName System.Drawing

function Get-CoverExtension([byte[]]$Data) {
    if (-not $Data -or $Data.Length -eq 0 -or $Data.Length -gt 67108864) { return $null }
    try {
        $stream = New-Object IO.MemoryStream(,$Data)
        try {
            $image = [Drawing.Image]::FromStream($stream, $false, $true)
            try {
                if ($image.RawFormat.Guid -eq [Drawing.Imaging.ImageFormat]::Jpeg.Guid) { return '.jpg' }
                if ($image.RawFormat.Guid -eq [Drawing.Imaging.ImageFormat]::Png.Guid) { return '.png' }
            } finally { $image.Dispose() }
        } finally { $stream.Dispose() }
    } catch { return $null }
    return $null
}

function Save-Cover($Result, $Meta) {
    $data = $Result.CoverData
    $extension = Get-CoverExtension $data
    if (-not $extension -and $Meta.albumPic) {
        $uri = $null
        if ([uri]::TryCreate([string]$Meta.albumPic, [UriKind]::Absolute, [ref]$uri) -and $uri.Scheme -in @('http','https')) {
            $temporary = [IO.Path]::GetTempFileName()
            try {
                Invoke-WebRequest -Uri $uri.AbsoluteUri -OutFile $temporary -UseBasicParsing -TimeoutSec 20 | Out-Null
                $data = [IO.File]::ReadAllBytes($temporary)
                $extension = Get-CoverExtension $data
            } catch { Write-Warning "albumPic 封面获取失败：$($_.Exception.Message)" }
            finally { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
        }
    }
    if (-not $extension) {
        Write-Host "封面下载失败：$([IO.Path]::GetFileName($Result.AudioPath))（NCM 内嵌封面和 albumPic 均无可用图片）" -ForegroundColor Yellow
        return $null
    }
    $coverPath = [IO.Path]::Combine([IO.Path]::GetDirectoryName($Result.AudioPath), [IO.Path]::GetFileNameWithoutExtension($Result.AudioPath) + '.cover' + $extension)
    try {
        [IO.File]::WriteAllBytes($coverPath, $data)
        Write-Host "封面已保存：$coverPath"
        return $coverPath
    } catch {
        Write-Host "封面下载失败：$([IO.Path]::GetFileName($Result.AudioPath))（保存图片失败：$($_.Exception.Message)）" -ForegroundColor Yellow
        return $null
    }
}

function Get-Lyric($Title, $Artist, $Cookie) {
    if (-not $Cookie) { return $null }
    $headers = @{ Cookie = $Cookie; Referer = 'https://music.163.com/'; 'User-Agent' = 'Mozilla/5.0' }
    $query = [uri]::EscapeDataString(($Title + ' ' + $Artist).Trim())
    try {
        $search = Invoke-RestMethod -Uri "https://music.163.com/api/search/get/web?s=$query&type=1&offset=0&total=true&limit=15" -Headers $headers -TimeoutSec 20
        $songs = @($search.result.songs)
        if ($songs.Count -eq 0) { return $null }
        $song = $songs | Sort-Object { if ($_.name -eq $Title) { 0 } else { 1 } } | Select-Object -First 1
        $data = Invoke-RestMethod -Uri "https://music.163.com/api/song/lyric?id=$($song.id)&lv=1&kv=1&tv=-1" -Headers $headers -TimeoutSec 20
        if ($data.lrc.lyric) { return [string]$data.lrc.lyric }
    } catch { Write-Warning "歌词获取失败：$($_.Exception.Message)" }
    return $null
}

function Get-NormalizedTrackText([string]$Value) {
    if (-not $Value) { return '' }
    return ([Text.RegularExpressions.Regex]::Replace($Value.Trim().ToLowerInvariant(), '[\s\p{P}\p{S}]+', ''))
}

function Get-NcmTrackInfo([string]$Path) {
    $file = $null
    try { $file = Get-Item -LiteralPath $Path -Force } catch { }
    $size = if ($file) { [long]$file.Length } else { 0L }
    $modifiedUtc = if ($file) { $file.LastWriteTimeUtc.ToString('o') } else { '' }
    $fallbackTitle = [IO.Path]::GetFileNameWithoutExtension($Path)
    try {
        if (-not $file) { throw '文件不存在或无法读取。' }
        $probe = [NcmDecoder]::ReadCoverFile($Path, [IO.Path]::GetDirectoryName($Path))
        $meta = $probe.MetadataJson | ConvertFrom-Json
        $title = if ($meta.musicName) { [string]$meta.musicName } else { $fallbackTitle }
        $artistNames = New-Object Collections.Generic.List[string]
        foreach ($entry in @($meta.artist)) {
            if ($null -eq $entry) { continue }
            if ($entry -is [array]) {
                foreach ($name in $entry) { if ($name) { $artistNames.Add([string]$name) } }
            } elseif ($entry) {
                $artistNames.Add([string]$entry)
            }
        }
        $artists = (($artistNames | Where-Object { $_ } | Select-Object -Unique) -join ' / ')
        $songId = [string]$meta.musicId
        if (-not $songId -or $songId -eq '0') { $songId = [string]$meta.id }
        if ($songId -eq '0') { $songId = '' }
        $songId = $songId.Trim()
        $key = if ($songId) {
            'id:' + $songId
        } else {
            'tag:' + (Get-NormalizedTrackText $title) + '|' + (($artistNames | Where-Object { $_ } | ForEach-Object { Get-NormalizedTrackText $_ }) -join '|')
        }
        $bitrate = 0L
        foreach ($property in @('bitrate', 'bitRate', 'br')) {
            if ($meta.PSObject.Properties.Name -contains $property) {
                [long]::TryParse([string]$meta.$property, [ref]$bitrate) | Out-Null
                if ($bitrate -gt 0) { break }
            }
        }
        $format = ([string]$meta.format).Trim().TrimStart('.').ToLowerInvariant()
        $formatRank = switch ($format) {
            'flac' { 4 }
            'alac' { 4 }
            'wav'  { 4 }
            'ape'  { 4 }
            'm4a'  { 3 }
            'aac'  { 3 }
            'ogg'  { 2 }
            'mp3'  { 1 }
            default { 0 }
        }
        [pscustomobject]@{
            Path = [IO.Path]::GetFullPath($file.FullName)
            Key = $key
            SongId = $songId
            Title = $title
            Artists = $artists
            Bitrate = $bitrate
            FormatRank = $formatRank
            Format = $format
            Size = $size
            ModifiedUtc = $modifiedUtc
            MetadataOk = $true
            Error = ''
        }
    } catch {
        [pscustomobject]@{
            Path = [IO.Path]::GetFullPath($Path)
            Key = ''
            SongId = ''
            Title = $fallbackTitle
            Artists = ''
            Bitrate = 0L
            FormatRank = 0
            Format = ''
            Size = $size
            ModifiedUtc = $modifiedUtc
            MetadataOk = $false
            Error = $_.Exception.Message
        }
    }
}

function Test-ReparsePoint([string]$Path) {
    try { return (([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) }
    catch { return $true }
}

function Get-SafeNcmFiles([string]$Root, [bool]$Recurse) {
    $files = New-Object Collections.Generic.List[string]
    $errors = New-Object Collections.Generic.List[string]
    $rootFull = [IO.Path]::GetFullPath($Root)
    if (-not [IO.Directory]::Exists($rootFull)) {
        throw "扫描目录不存在：$rootFull"
    }
    if ([IO.Path]::GetFileName($rootFull.TrimEnd('\')) -eq '.NeteaseToolboxTrash') {
        throw '工具箱回收站目录不能作为音乐扫描目录。'
    }
    $stack = New-Object 'Collections.Generic.Stack[string]'
    $stack.Push($rootFull)
    while ($stack.Count -gt 0) {
        $directory = $stack.Pop()
        if (Test-ReparsePoint $directory) {
            $errors.Add("已跳过重解析点目录：$directory")
            continue
        }
        try {
            foreach ($file in [IO.Directory]::EnumerateFiles($directory, '*.ncm', [IO.SearchOption]::TopDirectoryOnly)) {
                if (Test-ReparsePoint $file) {
                    $errors.Add("已跳过重解析点文件：$file")
                    continue
                }
                $files.Add([IO.Path]::GetFullPath($file))
            }
        } catch {
            $errors.Add("无法读取目录：$directory（$($_.Exception.Message)）")
        }
        if (-not $Recurse) { continue }
        try {
            foreach ($sub in [IO.Directory]::EnumerateDirectories($directory)) {
                if ([IO.Path]::GetFileName($sub) -eq '.NeteaseToolboxTrash') { continue }
                $stack.Push([IO.Path]::GetFullPath($sub))
            }
        } catch {
            $errors.Add("无法读取子目录：$directory（$($_.Exception.Message)）")
        }
    }
    [pscustomobject]@{
        Files = @($files | Sort-Object)
        Errors = @($errors)
    }
}

function Get-FileSha256([string]$Path) {
    try { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash }
    catch { return '' }
}

function Get-ConversionOutputKind([string]$Path) {
    $name = [IO.Path]::GetFileName($Path)
    if ($name -like '*.cover.*') { return 'Cover' }
    if ([IO.Path]::GetExtension($Path) -ieq '.lrc') { return 'Lyric' }
    return 'Audio'
}

function New-ConversionOutputRecord([string]$Path) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $item = Get-Item -LiteralPath $Path
    return [pscustomobject]@{
        Path = [IO.Path]::GetFullPath($item.FullName)
        Kind = (Get-ConversionOutputKind $item.FullName)
        Size = [long]$item.Length
        ModifiedUtc = $item.LastWriteTimeUtc.ToString('o')
        Sha256 = (Get-FileSha256 $item.FullName)
        GeneratedUtc = [DateTime]::UtcNow.ToString('o')
    }
}

function Save-ConversionLedger([string]$TaskId, [string]$CreatedUtc, $Records) {
    if (-not $env:LOCALAPPDATA) { return }
    $directory = Join-Path $env:LOCALAPPDATA 'NeteaseToolbox\Dedupe\conversions'
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $path = Join-Path $directory ($TaskId + '.json')
    $ledger = [pscustomobject]@{
        Version = 1
        TaskId = $TaskId
        CreatedUtc = $CreatedUtc
        Records = @($Records)
    }
    $temporary = $path + '.tmp-' + [guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($temporary, ($ledger | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
        if ([IO.File]::Exists($path)) {
            try { [IO.File]::Replace($temporary, $path, $null) }
            catch {
                [IO.File]::Copy($temporary, $path, $true)
                [IO.File]::Delete($temporary)
            }
        } else {
            [IO.File]::Move($temporary, $path)
        }
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

# Media cleanup is intentionally not performed in this script. The toolbox
# quarantine owns every destructive operation so it can be previewed and restored.
if ($DeduplicateOnly) {
    Write-Host '旧去重入口已停用：本次没有读取、移动或删除任何文件。请在工具箱“设置与维护”中使用“曲库安全去重”。' -ForegroundColor Yellow
    exit 0
}

if ($ExtractCover) {
    if (-not $CoverOutputDir) { throw '未指定封面保存目录。' }
    $coverDirectory = [IO.Path]::GetFullPath($CoverOutputDir)
    $coverFiles = @($Files | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($coverFiles.Count -eq 0) { throw '未指定要提取封面的 NCM 文件。' }
    $coverOk = 0; $coverFailed = 0
    foreach ($file in $coverFiles) {
        try {
            $result = [NcmDecoder]::ReadCoverFile($file, $coverDirectory)
            $meta = $result.MetadataJson | ConvertFrom-Json
            [IO.Directory]::CreateDirectory($coverDirectory) | Out-Null
            if (Save-Cover $result $meta) { $coverOk++ } else { $coverFailed++ }
        } catch {
            Write-Host "封面下载失败：$file（$($_.Exception.Message)）" -ForegroundColor Yellow
            $coverFailed++
        }
    }
    Write-Host "封面提取完成：成功 $coverOk，失败 $coverFailed。"
    if ($coverFailed -gt 0) { exit 1 }
    exit 0
}

function Resolve-PortablePath([string]$Value) {
    if (-not $Value) { return $null }
    if ([IO.Path]::IsPathRooted($Value)) { return [IO.Path]::GetFullPath($Value) }
    return [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Value))
}
$configPath = Join-Path $PSScriptRoot 'converter.settings.json'
if (-not $OutputDir -and $env:NCM_OUTPUT_DIR) { $OutputDir = [string]$env:NCM_OUTPUT_DIR }
$config = @{}
if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    try { $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { Write-Warning "配置文件读取失败：$($_.Exception.Message)" }
}

$inputRoot = if ($SourceRoot) { $SourceRoot } else { [string]$config.InputDirectory }
if ($inputRoot) {
    $inputRoot = (Resolve-PortablePath $inputRoot)
    if (-not (Test-Path -LiteralPath $inputRoot -PathType Container)) {
        Write-Warning "配置的输入目录不存在：$inputRoot"
        $inputRoot = $null
    }
}

if (-not $OutputDir) { $OutputDir = [string]$config.OutputDirectory }
if (-not $OutputDir) {
    $legacyOutputSetting = Join-Path $PSScriptRoot 'output-folder.txt'
    if (Test-Path -LiteralPath $legacyOutputSetting -PathType Leaf) {
        $OutputDir = [IO.File]::ReadAllText($legacyOutputSetting, [Text.Encoding]::UTF8).Trim()
    }
}
$customOutputRoot = if ($OutputDir) { (Resolve-PortablePath $OutputDir) } else { $null }

if ($ScanOnly) {
    if (-not $ScanJsonPath) { throw '未指定扫描结果文件。' }
    if (-not $inputRoot) { throw '未指定可读取的音乐扫描目录。' }
    $scanInput = Get-SafeNcmFiles $inputRoot ([bool]$Recurse)
    $records = New-Object System.Collections.ArrayList
    foreach ($scanPath in $scanInput.Files) {
        [void]$records.Add((Get-NcmTrackInfo $scanPath))
    }
    $scanObject = [pscustomobject]@{
        Version = 1
        Root = [IO.Path]::GetFullPath($inputRoot)
        CreatedUtc = [DateTime]::UtcNow.ToString('o')
        Recurse = [bool]$Recurse
        Files = @($records)
        Errors = @($scanInput.Errors)
    }
    $scanOutputPath = [IO.Path]::GetFullPath($ScanJsonPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($scanOutputPath)) | Out-Null
    [IO.File]::WriteAllText($scanOutputPath, ($scanObject | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    Write-Host "只读扫描完成：共 $($records.Count) 首歌曲；未修改任何文件。"
    exit 0
}

$files = @($Files | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) })
if ($files.Count -eq 0 -and $inputRoot) {
    $files = @((Get-SafeNcmFiles $inputRoot $true).Files)
}
if ($files.Count -eq 0) {
    $files = @(Select-NcmInputFiles)
    if ($files.Count -eq 0) { exit 0 }
}

$ffmpeg = Resolve-PortablePath ([string]$config.FfmpegPath)
if ($ffmpeg -and -not (Test-Path -LiteralPath $ffmpeg -PathType Leaf)) {
    Write-Warning "配置的 FFmpeg 路径无效：$ffmpeg"
    $ffmpeg = $null
}
if (-not $ffmpeg) {
    foreach ($candidate in @((Join-Path $PSScriptRoot 'ffmpeg.exe'), (Join-Path $PSScriptRoot 'ffmpeg\bin\ffmpeg.exe'))) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $ffmpeg = $candidate; break }
    }
}
if (-not $ffmpeg) { $ffmpeg = (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue).Source }
$cookie = $null
$sessionPath = Join-Path $env:LOCALAPPDATA 'NeteaseToolbox\Auth\session.dat'
if (Test-Path -LiteralPath $sessionPath) {
    try {
        Add-Type -AssemblyName System.Security
        $sessionBytes = [System.Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($sessionPath), $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        $session = [Text.Encoding]::UTF8.GetString($sessionBytes) | ConvertFrom-Json
        $cookie = [string]$session.cookie
    } catch { Write-Host '提示：无法读取工具箱登录状态，请重新扫码登录。' -ForegroundColor Yellow }
}

$conversionTaskId = if ($ConversionTaskId) { $ConversionTaskId } else {
    'ps-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
}
$conversionTaskId = ($conversionTaskId -replace '[^A-Za-z0-9._-]', '_')
if (-not $conversionTaskId) {
    $conversionTaskId = 'ps-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
}
$ledgerCreatedUtc = [DateTime]::UtcNow.ToString('o')
$ledgerRecords = New-Object System.Collections.ArrayList
if ($env:LOCALAPPDATA) {
    $ledgerPath = Join-Path $env:LOCALAPPDATA ("NeteaseToolbox\Dedupe\conversions\" + $conversionTaskId + '.json')
    if (Test-Path -LiteralPath $ledgerPath -PathType Leaf) {
        try {
            $existingLedger = Get-Content -LiteralPath $ledgerPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($existingLedger.CreatedUtc) { $ledgerCreatedUtc = [string]$existingLedger.CreatedUtc }
            foreach ($existingRecord in @($existingLedger.Records)) {
                if ($existingRecord) { [void]$ledgerRecords.Add($existingRecord) }
            }
        } catch {
            Write-Warning "已有转换来源记录无法读取：$($_.Exception.Message)"
        }
    }
}

$ok = 0; $failed = 0
foreach ($file in $files) {
    $source = $null
    $sourceHash = ''
    $sourceSongId = ''
    $recordOutputs = @()
    $recordSuccess = $false
    $recordError = ''
    try {
        $source = Get-Item -LiteralPath $file
        $sourceHash = Get-FileSha256 $source.FullName
        if ($customOutputRoot) {
            $sourceDir = [IO.Path]::GetFullPath($source.DirectoryName).TrimEnd('\')
            $relativeDir = $null
            if ($inputRoot) {
                $normalizedInputRoot = (Resolve-PortablePath $inputRoot).TrimEnd('\')
                if ($sourceDir.Equals($normalizedInputRoot, [StringComparison]::OrdinalIgnoreCase)) { $relativeDir = '' }
                else {
                    $prefix = $normalizedInputRoot + '\'
                    if ($sourceDir.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $relativeDir = $sourceDir.Substring($prefix.Length) }
                }
            }
            $outDir = if ($relativeDir) { Join-Path $customOutputRoot $relativeDir } else { $customOutputRoot }
        } else {
            $outDir = Join-Path $source.DirectoryName 'unlock'
        }
        $result = [NcmDecoder]::ConvertFile($source.FullName, $outDir)
        $meta = $result.MetadataJson | ConvertFrom-Json
        $sourceSongId = [string]$meta.musicId
        if (-not $sourceSongId -or $sourceSongId -eq '0') { $sourceSongId = [string]$meta.id }
        if ($sourceSongId -eq '0') { $sourceSongId = '' }
        $sourceSongId = $sourceSongId.Trim()
        $title = if ($meta.musicName) { [string]$meta.musicName } else { $source.BaseName }
        $album = [string]$meta.album
        $artist = @($meta.artist | ForEach-Object { if ($_ -is [array]) { [string]$_[0] } else { [string]$_ } }) -join ' / '
        $format = ([string]$meta.format).TrimStart('.')
        if (-not $format) { $format = [IO.Path]::GetExtension($result.AudioPath).TrimStart('.') }
        $coverPath = Save-Cover $result $meta
        $lyric = Get-Lyric $title $artist $cookie
        $tagged = Join-Path $outDir ($source.BaseName + '.tagged.' + $format)
        if ($ffmpeg -and (Test-Path -LiteralPath $ffmpeg)) {
            $ffArgs = @('-hide_banner','-loglevel','error','-y','-i',$result.AudioPath,'-map','0','-c','copy','-metadata',"title=$title",'-metadata',"artist=$artist",'-metadata',"album=$album")
            if ($coverPath) {
                $ffArgs = @('-hide_banner','-loglevel','error','-y','-i',$result.AudioPath,'-i',$coverPath,'-map','0:a:0','-map','1:v:0','-c:a','copy','-c:v','copy','-disposition:v:0','attached_pic','-metadata:s:v:0','title=Album cover','-metadata:s:v:0','comment=Cover (front)','-metadata',"title=$title",'-metadata',"artist=$artist",'-metadata',"album=$album")
            }
            if ($lyric) { $ffArgs += @('-metadata',"lyrics=$lyric") }
            $ffArgs += $tagged
            & $ffmpeg @ffArgs
            if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $tagged)) {
                [IO.File]::Copy($tagged, $result.AudioPath, $true)
                Remove-Item -LiteralPath $tagged -Force -ErrorAction SilentlyContinue
            }
            else {
                $ffmpegExit = $LASTEXITCODE
                if (Test-Path -LiteralPath $tagged) { Remove-Item -LiteralPath $tagged -Force }
                throw "FFmpeg 写入标签或封面失败（退出码 $ffmpegExit）；已解码音频和单独封面保留，可重新转换。"
            }
        }
        elseif ($coverPath) { Write-Host "封面嵌入失败：$($source.Name)；未找到 FFmpeg，图片已单独保存。" -ForegroundColor Yellow }
        $lrcPath = $null
        if ($lyric) {
            $lrcPath = [IO.Path]::ChangeExtension($result.AudioPath,'.lrc')
            [IO.File]::WriteAllText($lrcPath,$lyric,[Text.UTF8Encoding]::new($false))
        }
        $outputPaths = New-Object System.Collections.ArrayList
        foreach ($outputPath in @($result.AudioPath, $coverPath, $lrcPath)) {
            if (-not $outputPath) { continue }
            $outputRecord = New-ConversionOutputRecord $outputPath
            if ($outputRecord) { [void]$outputPaths.Add($outputRecord) }
        }
        $recordOutputs = @($outputPaths)
        $recordSuccess = $true
        Write-Host "完成：$($source.Name) -> $($result.AudioPath)" -ForegroundColor Green
        $ok++
    } catch {
        $recordError = $_.Exception.Message
        Write-Host "失败：$file`n$($recordError)" -ForegroundColor Red
        $failed++
    }
    $recordSourcePath = if ($source) { [IO.Path]::GetFullPath($source.FullName) } else { [IO.Path]::GetFullPath($file) }
    $record = [pscustomobject]@{
        Version = 1
        TaskId = $conversionTaskId
        SourcePath = $recordSourcePath
        SourceSongId = $sourceSongId
        SourceSize = if ($source) { [long]$source.Length } else { 0L }
        SourceModifiedUtc = if ($source) { $source.LastWriteTimeUtc.ToString('o') } else { '' }
        SourceSha256 = $sourceHash
        Success = $recordSuccess
        Error = $recordError
        ConvertedUtc = [DateTime]::UtcNow.ToString('o')
        Outputs = @($recordOutputs)
    }
    [void]$ledgerRecords.Add($record)
    try { Save-ConversionLedger $conversionTaskId $ledgerCreatedUtc $ledgerRecords }
    catch { Write-Warning "转换来源记录写入失败：$($_.Exception.Message)" }
}
Write-Host "`n转换完成：成功 $ok，失败 $failed。输出目录：$(if ($customOutputRoot) { $customOutputRoot } else { '各歌曲目录下的 unlock 文件夹' })。"
if ($failed -gt 0 -or -not $cookie) { Write-Host '提示：歌词获取需要先在工具箱左侧点击“登录网易云”并扫码登录。' -ForegroundColor Yellow }
if ($failed -gt 0) { exit 1 }
exit 0
