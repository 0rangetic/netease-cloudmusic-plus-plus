$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$facade = Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\netstandard') -Recurse -Filter netstandard.dll | Select-Object -First 1 -ExpandProperty FullName
$output = Join-Path $PSScriptRoot 'AudioForwardingValidation.exe'
& $compiler /nologo /target:exe /platform:x64 "/reference:$facade" "/reference:$PSScriptRoot\NAudio.Core.dll" "/reference:$PSScriptRoot\NAudio.Wasapi.dll" "/out:$output" "$PSScriptRoot\AudioForwardingValidation.cs" "$PSScriptRoot\AudioForwarder.cs" "$PSScriptRoot\ProcessAudioCapture.cs"
if ($LASTEXITCODE -ne 0) { throw 'Audio validation compilation failed.' }
$testFolder = Join-Path $env:TEMP ('ncm-audio-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testFolder | Out-Null
$helper = Join-Path $testFolder 'cloudmusic.exe'
$helperSource = Join-Path $testFolder 'Idle.cs'
[IO.File]::WriteAllText($helperSource, 'class Idle { static void Main() { System.Threading.Thread.Sleep(30000); } }')
& $compiler /nologo /target:exe "/out:$helper" $helperSource
if ($LASTEXITCODE -ne 0) { throw 'Idle helper compilation failed.' }
$previous = $env:NCM_AUDIO_TEST_CHILD
try {
    $env:NCM_AUDIO_TEST_CHILD = $helper
    # Run the test on an MTA worker inside .NET Framework PowerShell. Loading from bytes
    # also supports workspaces Windows classifies as remote. The production folder is local.
    $env:NCM_AUDIO_TEST_FOLDER = $PSScriptRoot
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -Command '$base=$env:NCM_AUDIO_TEST_FOLDER; [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $base "NAudio.Core.dll"))) | Out-Null; [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $base "NAudio.Wasapi.dll"))) | Out-Null; $assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $base "AudioForwardingValidation.exe"))); exit $assembly.EntryPoint.Invoke($null,@())'
    if ($LASTEXITCODE -ne 0) { throw 'Audio validation failed.' }
} finally {
    $env:NCM_AUDIO_TEST_CHILD = $previous
    Remove-Item Env:NCM_AUDIO_TEST_FOLDER -ErrorAction SilentlyContinue
    # Delete only the three known files created for this test; never recursively delete a computed path.
    Remove-Item -LiteralPath $helper,$helperSource -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $testFolder -ErrorAction SilentlyContinue
}
