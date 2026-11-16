param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Zainstaluj Visual Studio Build Tools z narzędziami .NET Desktop i .NET Framework 4.8 Targeting Pack.' }
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Nie znaleziono MSBuild.' }
& $msbuild (Join-Path $PSScriptRoot 'Postinstall.csproj') /t:Rebuild /nologo /verbosity:minimal /p:Configuration=Release
if ($LASTEXITCODE -ne 0) { throw 'Kompilacja nie powiodła się.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$exe = Join-Path $OutputDirectory 'Postinstall.exe'
Copy-Item (Join-Path $PSScriptRoot 'bin\Release\Postinstall.exe') $exe -Force
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $OutputDirectory 'Postinstall.exe.sha256') -Encoding ASCII -Value ($hash + '  Postinstall.exe')
Write-Output ('Gotowy pojedynczy plik EXE: ' + $exe)
