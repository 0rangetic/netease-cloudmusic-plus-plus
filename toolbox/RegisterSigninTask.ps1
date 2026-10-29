[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ExecutablePath,
    [ValidateRange(0,23)][int]$Hour = 8,
    [ValidateRange(0,59)][int]$Minute = 30,
    [ValidateSet('true','false')][string]$Enabled = 'true'
)
$ErrorActionPreference = 'Stop'
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw 'Toolbox executable is missing.' }
$scheduler = New-Object -ComObject 'Schedule.Service'
$scheduler.Connect()
$root = $scheduler.GetFolder('\')
$definition = $scheduler.NewTask(0)
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$definition.RegistrationInfo.Description = 'Netease Toolbox daily Yunbei and VIP check-in; credentials remain protected for the current Windows user.'
$definition.Principal.UserId = $identity
$definition.Principal.LogonType = 3 # InteractiveToken: no password, current user only.
$definition.Principal.RunLevel = 0
$definition.Settings.Enabled = $Enabled -eq 'true'
$definition.Settings.StartWhenAvailable = $true
$definition.Settings.DisallowStartIfOnBatteries = $false
$definition.Settings.StopIfGoingOnBatteries = $false
$definition.Settings.MultipleInstances = 2 # IgnoreNew.
$definition.Settings.ExecutionTimeLimit = 'PT10M'
$definition.Settings.RestartInterval = 'PT30M'
$definition.Settings.RestartCount = 3
$chinaNow = [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8))
$daily = $definition.Triggers.Create(2)
$daily.DaysInterval = 1
$daily.StartBoundary = '{0}T{1:00}:{2:00}:00+08:00' -f $chinaNow.ToString('yyyy-MM-dd'), $Hour, $Minute
$daily.Enabled = $true
$logon = $definition.Triggers.Create(9)
$logon.UserId = $identity
$logon.Delay = 'PT30S'
$logon.Enabled = $true
$action = $definition.Actions.Create(0)
$action.Path = $ExecutablePath
$action.Arguments = '--daily-signin --scheduled'
$action.WorkingDirectory = [IO.Path]::GetDirectoryName($ExecutablePath)
$registered = $root.RegisterTaskDefinition('NeteaseToolboxDailySignin', $definition, 6, $identity, $null, 3)
if ($registered.Definition.Actions.Item(1).Path -ne $ExecutablePath) { throw 'Registered task action does not match the toolbox.' }
Write-Output ('REGISTERED Enabled={0} Time={1:00}:{2:00}+08:00' -f $registered.Enabled, $Hour, $Minute)
