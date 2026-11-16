$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# Microsoft publishes the current bundle and matching dependencies together.
# AppX deployment verifies the Microsoft signatures. There is no script fetched and executed.
$folder = Join-Path $env:TEMP ('Postinstall-WinGet-' + [Guid]::NewGuid().ToString('N'))
New-Item $folder -ItemType Directory -Force | Out-Null
function Get-Asset($asset, $destination) {
    if (-not $asset -or $asset.browser_download_url -notlike 'https://github.com/microsoft/winget-cli/releases/download/*') {
        throw 'Nie znaleziono oficjalnego pliku WinGet.'
    }
    Write-Output ('Pobieranie ' + $asset.name)
    Invoke-WebRequest -UseBasicParsing -Uri $asset.browser_download_url -OutFile $destination
    if ($asset.digest -match '^sha256:(?<hash>[a-fA-F0-9]{64})$') {
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $Matches.hash) {
            throw 'Błędna suma SHA-256 pakietu WinGet.'
        }
    }
}
try {
    $release = Invoke-RestMethod 'https://api.github.com/repos/microsoft/winget-cli/releases/latest' -Headers @{'User-Agent'='Postinstall/1.0'}
    $bundle = $release.assets | Where-Object { $_.name -match '^Microsoft\.DesktopAppInstaller.*\.msixbundle$' } | Select-Object -First 1
    $dependencies = $release.assets | Where-Object { $_.name -eq 'DesktopAppInstaller_Dependencies.zip' } | Select-Object -First 1
    Get-Asset $bundle (Join-Path $folder 'WinGet.msixbundle')
    Get-Asset $dependencies (Join-Path $folder 'Dependencies.zip')
    Expand-Archive -LiteralPath (Join-Path $folder 'Dependencies.zip') -DestinationPath (Join-Path $folder 'Dependencies')
    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } elseif ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' }
    $packages = Get-ChildItem (Join-Path $folder 'Dependencies') -Recurse -File | Where-Object {
        $_.Extension -in @('.appx','.msix','.appxbundle','.msixbundle') -and
        ($_.FullName -match ('[\\/]' + $arch + '[\\/]') -or $_.Name -match ('_' + $arch + '_') -or $_.Name -match '_neutral_')
    }
    if (-not $packages) { throw ('Brak zależności WinGet dla ' + $arch) }
    Write-Output 'Instalowanie Microsoft App Installer i jego zależności…'
    # Only skip a dependency when an equal or newer package is registered for this user.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($package in $packages) {
        $skip = $false
        if ($package.Extension -in @('.appx','.msix')) {
            $zip = [IO.Compression.ZipFile]::OpenRead($package.FullName)
            try {
                $entry = $zip.GetEntry('AppxManifest.xml')
                if ($entry) {
                    $reader = New-Object IO.StreamReader($entry.Open())
                    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
                    $identity = $manifest.Package.Identity
                    $skip = @(Get-AppxPackage -Name $identity.Name | Where-Object {
                        [version]$_.Version -ge [version]$identity.Version -and
                        ($_.Architecture -eq $arch -or $_.Architecture -eq 'Neutral')
                    }).Count -gt 0
                }
            } finally { $zip.Dispose() }
        }
        if (-not $skip) { Add-AppxPackage -Path $package.FullName }
    }
    Add-AppxPackage -Path (Join-Path $folder 'WinGet.msixbundle')
    Write-Output 'WinGet został przygotowany.'
} catch {
    [Console]::Error.WriteLine(($_ | Out-String))
    exit 1
} finally {
    Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
}
