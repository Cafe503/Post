$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools -property installationPath
if (-not $installation) { throw 'Nie znaleziono Build Tools dla .NET Desktop.' }
Import-Module (Join-Path $installation 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $installation -SkipAutomaticLocation -DevCmdArguments '-arch=amd64 -host_arch=amd64'
$env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
if (Test-Path Z:\projects) { Set-Location Z:\projects }
Write-Host 'WPF: dotnet build <projekt.csproj> -c Release'
Write-Host '.NET Framework: msbuild <projekt.sln> /restore /p:RestorePackagesConfig=true /p:Configuration=Release'

