param(
    [ValidateSet('Start', 'Stop', 'Status')][string]$Action = 'Start',
    [string]$ClientPath,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding
$bundleNode = Join-Path (Split-Path $PSScriptRoot -Parent) 'runtime\node.exe'
$nodePath = if (Test-Path -LiteralPath $bundleNode) { $bundleNode } else { (Get-Command node -ErrorAction Stop).Source }
$runtimeScript = Join-Path $PSScriptRoot 'control.mjs'
$stateDir = Join-Path $env:LOCALAPPDATA 'Netease\CloudMusic\CodexMulti'
$supportedVersions = @('3.1.41.205529', '3.1.42.205594')
$patchVersion = '0.4.1'
$lastStatusError = ''
$resolvedClientVersion = ''

function Read-Targets {
    $result = Invoke-RestMethod -Uri 'http://127.0.0.1:9222/json/list' -TimeoutSec 2
    if (-not ($result | Where-Object { $_.type -eq 'page' -and $_.url -eq 'orpheus://orpheus/pub/app.html' })) {
        throw '9222 端口未提供网易云主页面，可能被其他程序占用。'
    }
    return $result
}
function Invoke-Control([string]$Mode, [int]$TimeoutMilliseconds = 12000) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $nodePath
    $info.Arguments = '"' + $runtimeScript + '" ' + $Mode
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $commandProcess = New-Object System.Diagnostics.Process
    $commandProcess.StartInfo = $info
    try {
        if (-not $commandProcess.Start()) { throw '无法启动补丁控制脚本。' }
        $outputTask = $commandProcess.StandardOutput.ReadToEndAsync()
        $errorTask = $commandProcess.StandardError.ReadToEndAsync()
        if (-not $commandProcess.WaitForExit($TimeoutMilliseconds)) {
            try { $commandProcess.Kill(); $null = $commandProcess.WaitForExit(3000) } catch {}
            throw ('补丁 ' + $Mode + ' 超时，请检查客户端和任务消息后重试。')
        }
        return [pscustomobject]@{ ExitCode = $commandProcess.ExitCode; Output = $outputTask.Result.Trim(); ErrorText = $errorTask.Result.Trim() }
    } finally { $commandProcess.Dispose() }
}
function Read-PatchStatus([int]$TimeoutMilliseconds = 12000) {
    $script:lastStatusError = ''
    try {
        $result = Invoke-Control 'status' $TimeoutMilliseconds
        if ($result.ExitCode -ne 0) {
            $script:lastStatusError = $result.ErrorText
            return $null
        }
        return ($result.Output | ConvertFrom-Json)
    } catch { $script:lastStatusError = $_.Exception.Message; return $null }
}
function Test-PatchReady($Status) {
    if (-not $Status -or -not $Status.installed) { return $false }
    if ($Status.PSObject.Properties['clickHookReady']) { return [bool]$Status.clickHookReady }
    return [bool]$Status.clickHook
}
function Find-ClientMainProcess([string]$ExpectedPath, [switch]$Starting) {
    $processes = @(Get-CimInstance Win32_Process -Filter "Name='cloudmusic.exe'" -ErrorAction Stop)
    $main = @()
    $children = 0
    foreach ($clientProcess in $processes) {
        if ($clientProcess.CommandLine -match '(?:^|\s)"?--type(?:=|\s|"|$)') { $children++; continue }
        if (-not $clientProcess.CommandLine -or -not $clientProcess.ExecutablePath) {
            throw '无法核实网易云主进程，请使用与客户端相同的 Windows 用户和权限运行补丁。'
        }
        $main += $clientProcess
    }
    if ($main.Count -gt 1) { throw '发现多个网易云主进程，请只保留要连接的实例后重试。' }
    if ($main.Count -eq 0) {
        if (-not $Starting -and $children -gt 0) { throw '只发现网易云辅助进程，请等待主界面正常打开后重试。' }
        return $null
    }
    if ([System.IO.Path]::GetFullPath($main[0].ExecutablePath) -ine [System.IO.Path]::GetFullPath($ExpectedPath)) {
        throw '正在运行的网易云来自其他安装目录，请使用该目录的 cloudmusic.exe 后重试。'
    }
    return $main[0]
}
function Find-NativePython {
    $explicit = $env:NCM_MULTI_PYTHON
    $candidates = if ($explicit) { @($explicit) } else { @(
        (Join-Path (Split-Path $PSScriptRoot -Parent) 'runtime\python\python.exe'),
        (Join-Path (Split-Path $PSScriptRoot -Parent) 'toolbox\runtime\python\python.exe')
    ) }
    foreach ($candidate in $candidates) {
        if (-not [System.IO.Path]::IsPathRooted($candidate) -or -not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $probe = New-Object System.Diagnostics.Process
        $probe.StartInfo = New-Object System.Diagnostics.ProcessStartInfo
        $probe.StartInfo.FileName = $candidate
        $probe.StartInfo.Arguments = '-c "import frida, psutil"'
        $probe.StartInfo.UseShellExecute = $false
        $probe.StartInfo.CreateNoWindow = $true
        $probe.StartInfo.RedirectStandardOutput = $true
        $probe.StartInfo.RedirectStandardError = $true
        try {
            if (-not $probe.Start()) { continue }
            $null = $probe.StandardOutput.ReadToEndAsync()
            $null = $probe.StandardError.ReadToEndAsync()
            if (-not $probe.WaitForExit(4000)) { try { $probe.Kill(); $null = $probe.WaitForExit(1000) } catch {}; continue }
            if ($probe.ExitCode -eq 0) { return [System.IO.Path]::GetFullPath($candidate) }
        } catch { continue } finally { $probe.Dispose() }
    }
    if ($explicit) { throw 'NCM_MULTI_PYTHON 指定的 Python 不可用或缺少 frida、psutil，请修复后重试。' }
    throw '网易云已普通启动；后开补丁需要包含 frida、psutil 的 Python。请使用工具箱随包运行环境、提供 runtime\python，或将 NCM_MULTI_PYTHON 设为完整 python.exe 路径。只有 Node 的便携包仍可先从托盘退出网易云，再启动补丁。'
}
function Find-Client {
    if ($ClientPath) {
        if (-not (Test-Path -LiteralPath $ClientPath -PathType Leaf)) { throw '指定的 cloudmusic.exe 不存在。' }
        return (Get-Item -LiteralPath $ClientPath).FullName
    }
    $running = @(Get-CimInstance Win32_Process -Filter "Name='cloudmusic.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath } | Select-Object -ExpandProperty ExecutablePath -Unique)
    if ($running.Count -eq 1) { return $running[0] }
    if ($running.Count -gt 1) { throw '检测到多个网易云安装目录，请先退出其他实例。' }
    $saved = Join-Path $stateDir 'client-path.txt'
    if (Test-Path -LiteralPath $saved) {
        $savedPath = (Get-Content -LiteralPath $saved -Raw).Trim()
        if (Test-Path -LiteralPath $savedPath -PathType Leaf) { return $savedPath }
    }
    foreach ($key in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\cloudmusic.exe',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\App Paths\cloudmusic.exe',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\cloudmusic.exe'
    )) {
        if (Test-Path -LiteralPath $key) {
            $candidate = (Get-Item -LiteralPath $key).GetValue('')
            if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $candidate }
        }
    }
    foreach ($directory in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)) {
        if ($directory) {
            $candidate = Join-Path $directory 'Netease\CloudMusic\cloudmusic.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    if ($CheckOnly) { throw '未自动找到网易云安装位置；可使用 -ClientPath 指定。' }
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = '请选择网易云音乐安装目录中的 cloudmusic.exe'
    $dialog.Filter = '网易云音乐 (cloudmusic.exe)|cloudmusic.exe'
    $dialog.CheckFileExists = $true
    try {
        if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { throw '已取消选择网易云安装目录。' }
        return $dialog.FileName
    } finally { $dialog.Dispose() }
}
function Validate-Client([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    if ($file.Name -ne 'cloudmusic.exe') { throw '请选择 cloudmusic.exe，不能选择工具箱或其他程序。' }
    $actualVersion = $file.VersionInfo.FileVersion
    if ($supportedVersions -notcontains $actualVersion) {
        throw ('当前网易云版本为 ' + $actualVersion + '；本补丁仅支持已实测的 ' + ($supportedVersions -join ' / ') + ' x64。')
    }
    $script:resolvedClientVersion = $actualVersion
    $stream = [System.IO.File]::OpenRead($file.FullName)
    $reader = New-Object System.IO.BinaryReader($stream)
    try {
        $stream.Position = 0x3C; $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw '请选择网易云 x64 客户端，32 位版本不支持。' }
    } finally { $reader.Dispose(); $stream.Dispose() }
}

try {
    if (-not [Environment]::Is64BitOperatingSystem) { throw '本便携包需要 64 位 Windows。' }
    if ($Action -eq 'Stop') {
        $current = Read-PatchStatus
        if (-not $current) { throw ('未能读取补丁状态，尚未确认停用：' + $lastStatusError) }
        if (-not $current.installed) { Write-Host '当前会话没有安装补丁。'; exit 0 }
        if ($current.version -ne $patchVersion) { throw '当前运行的是其他版本补丁。请先在原面板退出并停用，再启动本版本。' }
        Write-Host '正在退出房间并停用补丁。'
        $result = Invoke-Control 'shutdown' 30000
        if ($result.ExitCode -ne 0) { throw ('退出未确认成功：' + $result.ErrorText) }
        if ($result.Output) { Write-Output $result.Output }
        Write-Host '已退出房间并停用补丁。网易云客户端继续运行。'
        exit 0
    }
    if ($Action -eq 'Status') {
        $result = Invoke-Control 'status'
        if ($result.ExitCode -ne 0) { throw ('状态查询失败：' + $result.ErrorText) }
        Write-Output $result.Output
        exit 0
    }
    $resolvedClient = Find-Client
    Validate-Client $resolvedClient
    if ($CheckOnly) {
        Write-Output ([pscustomobject]@{ Compatible = $true; ClientVersion = $resolvedClientVersion; NodeVersion = (& $nodePath --version); ClientPath = $resolvedClient } | ConvertTo-Json)
        exit 0
    }
    New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $stateDir 'client-path.txt'), $resolvedClient, (New-Object System.Text.UTF8Encoding))
    $connected = $false
    try { $null = Read-Targets; $connected = $true } catch {}
    $mainProcess = Find-ClientMainProcess $resolvedClient
    if ($connected -and -not $mainProcess) { throw '调试端口已响应，但没有找到已验证的网易云主进程，请确认客户端正常启动后重试。' }
    $env:NCM_LISTEN_CLIENT_EXE = $resolvedClient
    if ($mainProcess) { $env:NCM_LISTEN_CLIENT_PID = [string]$mainProcess.ProcessId }
    else { Remove-Item Env:\NCM_LISTEN_CLIENT_PID -ErrorAction SilentlyContinue }
    if (-not $connected) {
        if ($mainProcess) {
            $env:NCM_MULTI_PYTHON = Find-NativePython
            Write-Host '网易云已在运行，正在通过本机热接入加载补丁，播放会保持运行。'
        } else {
            Write-Host '正在启动网易云并连接多人一起听补丁。'
            Start-Process -FilePath $resolvedClient -ArgumentList '--remote-debugging-address=127.0.0.1 --remote-debugging-port=9222'
            for ($attempt = 0; $attempt -lt 40; $attempt++) {
                Start-Sleep -Milliseconds 500
                try { $null = Read-Targets; $connected = $true; break } catch {}
            }
            if (-not $connected) { throw '未连接到网易云主页面。请等待启动完成后重试，并检查 9222 端口是否被占用。' }
            $mainProcess = Find-ClientMainProcess $resolvedClient -Starting
            if (-not $mainProcess) { throw '未能核实已启动的网易云主进程，请等待主界面正常打开后重试。' }
            $env:NCM_LISTEN_CLIENT_PID = [string]$mainProcess.ProcessId
        }
    }
    $current = Read-PatchStatus 4000
    if ($current -and $current.installed) {
        if (-not (Test-PatchReady $current)) { throw '已有补丁正在加载，请等待片刻后查看状态；本次没有重复启动。' }
        Write-Host ('补丁已在当前网易云会话运行，版本 ' + $current.version + '。')
        if ($current.version -ne $patchVersion) { Write-Host '如需使用当前版本，请先在原面板退出并停用补丁，再重新启动。' }
        exit 0
    }
    $worker = Start-Process -FilePath $nodePath -ArgumentList ('"' + $runtimeScript + '" watch') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $stateDir 'bridge.log') -RedirectStandardError (Join-Path $stateDir 'bridge-error.log')
    $ready = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $current = Read-PatchStatus 4000
        if ((Test-PatchReady $current) -and $current.version -eq $patchVersion) { $ready = $true; break }
        $worker.Refresh()
        if ($worker.HasExited) { break }
    }
    if (-not $ready) {
        $errorLog = Join-Path $stateDir 'bridge-error.log'
        $detail = if (Test-Path -LiteralPath $errorLog) { ([string](Get-Content -LiteralPath $errorLog -Raw -Encoding UTF8)).Trim() } else { '' }
        throw ('补丁启动未确认完成。' + $detail + ' 请查看网易云面板和 ' + $errorLog)
    }
    if ($current.clickHook) { Write-Host ('启动成功。补丁版本 ' + $current.version + '。请在网易云右上角的多人面板操作。') }
    else { Write-Host ('补丁已启动，版本 ' + $current.version + '；邀请点击入口未连接，请使用面板“读取私信邀请”或粘贴邀请链接。') }
} catch {
    Write-Host ('未完成：' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
