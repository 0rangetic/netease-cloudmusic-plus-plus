param(
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'),
    [string]$PythonDirectory
)
$ErrorActionPreference = 'Stop'
$version = '0.4.2'
$nodePath = (Get-Command node -ErrorAction Stop).Source
if ((& $nodePath -p 'process.arch') -ne 'x64') { throw '需要 x64 Node 运行环境。' }
if ((& $nodePath --version) -ne 'v24.19.0') { throw '运行环境版本变化，须先更新许可证和说明书再打包。' }
if ($PythonDirectory) {
    if (-not (Test-Path -LiteralPath $PythonDirectory -PathType Container)) { throw 'PythonDirectory 必须是完整的 Python 运行环境目录。' }
    $PythonDirectory = (Resolve-Path -LiteralPath $PythonDirectory).Path
    $pythonExecutable = Join-Path $PythonDirectory 'python.exe'
    if (-not (Test-Path -LiteralPath $pythonExecutable -PathType Leaf)) { throw 'PythonDirectory 中缺少 python.exe。' }
    if (-not @(Get-ChildItem -LiteralPath $PythonDirectory -File -Filter 'LICENSE*').Count) { throw 'PythonDirectory 中缺少 Python 完整许可证，请先补齐后再打包。' }
    $probe = New-Object System.Diagnostics.Process
    $probe.StartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $probe.StartInfo.FileName = $pythonExecutable
    $probe.StartInfo.Arguments = '-c "import frida, psutil"'
    $probe.StartInfo.UseShellExecute = $false
    $probe.StartInfo.CreateNoWindow = $true
    $probe.StartInfo.RedirectStandardOutput = $true
    $probe.StartInfo.RedirectStandardError = $true
    try {
        if (-not $probe.Start()) { throw '无法启动指定的 Python 运行环境。' }
        $null = $probe.StandardOutput.ReadToEndAsync()
        $null = $probe.StandardError.ReadToEndAsync()
        if (-not $probe.WaitForExit(5000)) { try { $probe.Kill(); $null = $probe.WaitForExit(1000) } catch {}; throw 'Python 依赖检查超时。' }
        if ($probe.ExitCode -ne 0) { throw 'PythonDirectory 必须已包含可导入的 frida、psutil。' }
    } finally { $probe.Dispose() }
}
$packageName = '网易云多人一起听补丁-v' + $version + '-win-x64'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$packageDirectory = Join-Path $OutputDirectory $packageName
$zipPath = $packageDirectory + '.zip'
if ((Test-Path -LiteralPath $packageDirectory) -or (Test-Path -LiteralPath $zipPath)) { throw '目标发布目录或 ZIP 已存在，请换一个构建输出目录。' }
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
$appDirectory = Join-Path $packageDirectory 'app'
$runtimeDirectory = Join-Path $packageDirectory 'runtime'
New-Item -ItemType Directory -Path $appDirectory,$runtimeDirectory -Force | Out-Null
foreach ($file in @('control.mjs','protocol.mjs','runtime.js','version.mjs','start.ps1',
    'native-client.mjs','native-host.mjs','native_bridge.py','native-agent.js')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $appDirectory $file)
}
# Windows PowerShell 5.1 needs a BOM for Chinese source text.
$starter = Join-Path $appDirectory 'start.ps1'
$starterText = [System.IO.File]::ReadAllText($starter)
[System.IO.File]::WriteAllText($starter, $starterText, (New-Object System.Text.UTF8Encoding($true)))
Copy-Item -LiteralPath $nodePath -Destination (Join-Path $runtimeDirectory 'node.exe')
if ($PythonDirectory) {
    # Copy the complete caller-supplied runtime, including all component licenses.
    # Never download Python/packages or copy a machine's app-state directories.
    Copy-Item -LiteralPath $PythonDirectory -Destination (Join-Path $runtimeDirectory 'python') -Recurse
}
Invoke-WebRequest -UseBasicParsing -Uri ('https://raw.githubusercontent.com/nodejs/node/v24.19.0/LICENSE') -OutFile (Join-Path $runtimeDirectory 'LICENSE.txt')
if ((Get-Item -LiteralPath (Join-Path $runtimeDirectory 'LICENSE.txt')).Length -lt 10000) { throw 'Node 完整许可证下载不完整。' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD_PARTY_NOTICES.txt') -Destination $packageDirectory
$manual = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'manual.md'))
[System.IO.File]::WriteAllText((Join-Path $packageDirectory '使用说明.txt'), $manual, (New-Object System.Text.UTF8Encoding($true)))
& $nodePath (Join-Path $PSScriptRoot 'build-manual.mjs') (Join-Path $packageDirectory '使用说明.html')
if ($LASTEXITCODE -ne 0) { throw 'HTML 说明书生成失败。' }
foreach ($entry in @(@('启动补丁.cmd','Start'),@('退出并停用.cmd','Stop'),@('查看状态.cmd','Status'))) {
    $cmdText = '@echo off' + "`r`n" + 'setlocal' + "`r`n" + ('title NetEase Multiplayer Patch ' + $version) + "`r`n" + 'powershell.exe -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0app\start.ps1" -Action ' + $entry[1] + "`r`n" + 'echo.' + "`r`n" + 'pause' + "`r`n"
    [System.IO.File]::WriteAllText((Join-Path $packageDirectory $entry[0]), $cmdText, [System.Text.Encoding]::ASCII)
}
$checksums = foreach ($file in Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Sort-Object FullName) {
    $relative = $file.FullName.Substring($packageDirectory.Length + 1).Replace('\','/')
    (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative
}
[System.IO.File]::WriteAllLines((Join-Path $packageDirectory '文件校验.sha256'), $checksums, (New-Object System.Text.UTF8Encoding))
Compress-Archive -LiteralPath $packageDirectory -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText(($zipPath + '.sha256'), $zipHash + '  ' + $packageName + '.zip' + "`n", (New-Object System.Text.UTF8Encoding))
Get-Item -LiteralPath $zipPath | Select-Object FullName,Length
Write-Output ('SHA256: ' + $zipHash)
