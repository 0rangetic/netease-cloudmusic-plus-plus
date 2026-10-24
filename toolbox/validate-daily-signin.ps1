[CmdletBinding()]
param([switch]$Live)
$ErrorActionPreference='Stop'
$validationRoot=Join-Path $env:LOCALAPPDATA ('NeteaseSignTrial\validation-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null
$validationExe=Join-Path $validationRoot 'DailySigninValidation.exe'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Security.dll /reference:System.Web.Extensions.dll /reference:System.Numerics.dll /reference:System.Core.dll "/out:$validationExe" (Join-Path $PSScriptRoot 'DailySigninValidation.cs') (Join-Path $PSScriptRoot 'DailySigninService.cs') (Join-Path $PSScriptRoot 'NeteaseAuth.cs')
if ($LASTEXITCODE -ne 0) { throw 'Daily signin validation compilation failed.' }
if ($Live) { & $validationExe --live } else { & $validationExe }
if ($LASTEXITCODE -ne 0) { throw 'Daily signin validation failed.' }
