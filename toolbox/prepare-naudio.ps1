$ErrorActionPreference = 'Stop'
# Pinned MIT-licensed NAudio 2.2.1 assemblies compatible with .NET Framework 4.8.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packages = @{
    'naudio.core' = '794645DBFD30E4880663D52D9D9224D55C301FA54228FF623C95D572C7887347'
    'naudio.wasapi' = 'C8396B8DBAB86A2619EDFD9158BEAF18C21CB86677099B5594950FD8651B9D1A'
}
foreach ($id in $packages.Keys) {
    $package = Join-Path $env:TEMP ($id + '-' + [guid]::NewGuid().ToString('N') + '.zip')
    try {
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$id/2.2.1/$id.2.2.1.nupkg" -OutFile $package
        if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $packages[$id]) { throw 'NAudio package checksum mismatch.' }
        $archive = [IO.Compression.ZipFile]::OpenRead($package)
        try {
            foreach ($entry in $archive.Entries | Where-Object { $_.FullName -match '^lib/netstandard2.0/[^/]+\.dll$' }) {
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $PSScriptRoot $entry.Name), $true)
                Unblock-File -LiteralPath (Join-Path $PSScriptRoot $entry.Name)
            }
        } finally { $archive.Dispose() }
    } finally { if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package } }
}
