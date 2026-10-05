$ErrorActionPreference = 'Stop'
# Build-time SDK only; the Evergreen WebView2 Runtime is installed separately.
$version = '1.0.4258.31'
$expected = '56F7F4B8BF9AEE4B8EFEFBBDD4F67D5F74EBD1B100ED0806DA71BF76AF481AA9'
$uri = "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$version/microsoft.web.webview2.$version.nupkg"
$package = Join-Path $env:TEMP ('ncm-webview2-' + [guid]::NewGuid().ToString('N') + '.zip')
try {
    Invoke-WebRequest -Uri $uri -OutFile $package
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $expected) { throw 'WebView2 SDK checksum mismatch.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entries = @{
            'lib/net462/Microsoft.Web.WebView2.Core.dll' = 'Microsoft.Web.WebView2.Core.dll'
            'lib/net462/Microsoft.Web.WebView2.WinForms.dll' = 'Microsoft.Web.WebView2.WinForms.dll'
            'runtimes/win-x64/native/WebView2Loader.dll' = 'WebView2Loader.dll'
        }
        foreach ($key in $entries.Keys) {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry($key), (Join-Path $PSScriptRoot $entries[$key]), $true)
        }
    } finally { $archive.Dispose() }
} finally { if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package } }
