[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$setupRoot = 'C:\WpfSetup'
$cache = Join-Path $setupRoot 'downloads'
$share = '\\host.lan\Data'
New-Item -ItemType Directory -Force -Path $setupRoot, $cache | Out-Null
Start-Transcript -Path (Join-Path $setupRoot 'setup.log') -Append

function Write-SetupStatus {
    param([string]$State, [string]$Message, [object]$Details = $null)
    $status = [ordered]@{
        state = $State
        message = $Message
        timestamp = (Get-Date).ToUniversalTime().ToString('o')
        details = $Details
    } | ConvertTo-Json -Depth 8
    $status | Set-Content -Path (Join-Path $setupRoot 'status.json') -Encoding UTF8
    try {
        $remote = Join-Path $share 'status'
        New-Item -ItemType Directory -Force -Path $remote | Out-Null
        $status | Set-Content -Path (Join-Path $remote 'wpf-setup.json') -Encoding UTF8
        Copy-Item (Join-Path $setupRoot 'setup.log') (Join-Path $remote 'setup.log') -Force -ErrorAction SilentlyContinue
    } catch { Write-Warning "Nie udalo sie zapisac statusu na hoście: $_" }
    Write-Host "[$State] $Message"
}

function Get-SetupFile {
    param([string]$Url, [string]$Destination, [string]$Sha512 = '')
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            if (-not (Test-Path $Destination)) {
                Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Destination -TimeoutSec 1800
            }
            if ($Sha512 -and (Get-FileHash $Destination -Algorithm SHA512).Hash -ne $Sha512) {
                Remove-Item $Destination -Force
                throw "Nieprawidlowa suma SHA512: $Destination"
            }
            return
        } catch {
            Remove-Item $Destination -Force -ErrorAction SilentlyContinue
            if ($attempt -eq 3) { throw }
            Start-Sleep -Seconds (5 * $attempt)
        }
    }
}

function Invoke-SetupInstaller {
    param([string]$File, [string[]]$InstallerArguments)
    $signature = Get-AuthenticodeSignature $File
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "Nieprawidlowy podpis Microsoft: $File ($($signature.Status))"
    }
    $process = Start-Process -FilePath $File -ArgumentList $InstallerArguments -Wait -PassThru
    if ($process.ExitCode -notin @(0, 3010)) { throw "Instalator $File zwrocil kod $($process.ExitCode)." }
    if ($process.ExitCode -eq 3010) { $script:rebootRequired = $true }
}

function Invoke-DotNet {
    param([string[]]$CliArguments)
    & "$env:ProgramFiles\dotnet\dotnet.exe" @CliArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($CliArguments -join ' ') zwrocil kod $LASTEXITCODE." }
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Uruchom skrypt jako administrator.'
    }
    Write-SetupStatus 'installing' 'Instalacja narzedzi WPF.'

    # Agent zapewnia wykonanie kompilacji i kontrolę VM z hosta bez sesji RDP.
    if (-not (Get-Service QEMU-GA -ErrorAction SilentlyContinue)) {
        $agent = Get-Item 'C:\OEM\qemu-ga-x86_64.msi' -ErrorAction SilentlyContinue
        if (-not $agent) { throw 'Brak instalatora QEMU Guest Agent w C:\OEM.' }
        $expectedHash = (Get-Content 'C:\OEM\qemu-ga.sha256' -Raw).Trim()
        if ((Get-FileHash $agent.FullName -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Nieprawidlowa suma SHA256 QEMU Guest Agent.' }
        if ($agent) {
            $process = Start-Process msiexec.exe -ArgumentList @('/i', ('"' + $agent.FullName + '"'), '/qn', '/norestart') -Wait -PassThru
            if ($process.ExitCode -notin @(0, 3010)) { throw "QEMU Guest Agent: $($process.ExitCode)" }
        }
    }

    $bootstrapper = Join-Path $cache 'vs_buildtools.exe'
    Get-SetupFile 'https://aka.ms/vs/stable/vs_buildtools.exe' $bootstrapper
    Write-SetupStatus 'installing' 'Visual Studio Build Tools 2026: .NET Desktop, MSBuild, NuGet, Windows SDK, .NET Framework 4.8/4.8.1.'
    Invoke-SetupInstaller $bootstrapper @(
        '--quiet', '--wait', '--norestart', '--nocache',
        '--installPath', 'C:\BuildTools',
        '--add', 'Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools',
        '--includeRecommended',
        '--add', 'Microsoft.Net.ComponentGroup.4.8.1.DeveloperTools',
        '--add', 'Microsoft.VisualStudio.Component.Windows11SDK.26100'
    )

    $sdkVersions = [ordered]@{}
    foreach ($channel in @('8.0', '10.0')) {
        $metadata = Invoke-RestMethod -Uri "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$channel/releases.json"
        $version = $metadata.'latest-sdk'
        $sdk = $metadata.releases | ForEach-Object { $_.sdks } | Where-Object { $_.version -eq $version } | Select-Object -First 1
        if (-not $sdk) { throw "Brak metadanych SDK $version." }
        $file = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.exe' } | Select-Object -First 1
        if (-not $file) { throw "Brak instalatora Windows x64 SDK $version." }
        Write-SetupStatus 'installing' "Instalacja .NET SDK $version."
        $installer = Join-Path $cache "dotnet-sdk-$version-win-x64.exe"
        Get-SetupFile $file.url $installer $file.hash
        Invoke-SetupInstaller $installer @('/install', '/quiet', '/norestart')
        $sdkVersions[$channel] = $version
    }

    $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msbuild = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw 'Nie znaleziono MSBuild z .NET Desktop Build Tools.' }
    Invoke-DotNet @('--info')
    Invoke-DotNet @('--list-sdks')
    Invoke-DotNet @('--list-runtimes')

    Write-SetupStatus 'verifying' 'Kompilacja przykladow WPF dla .NET 8, .NET 10, .NET Framework 4.8 i 4.8.1.'
    $checks = @()
    foreach ($channel in @('8.0', '10.0')) {
        $work = Join-Path $setupRoot "smoke-net$channel"
        New-Item -ItemType Directory -Force -Path $work | Out-Null
        @{ sdk = @{ version = $sdkVersions[$channel]; rollForward = 'latestPatch' } } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $work 'global.json') -Encoding UTF8
        Push-Location $work
        try {
            Invoke-DotNet @('new', 'wpf', '--name', 'WpfSmoke', '--output', '.', '--framework', "net$channel", '--no-restore', '--force')
            Invoke-DotNet @('build', 'WpfSmoke.csproj', '--configuration', 'Release', '--nologo')
        } finally { Pop-Location }
        $checks += @{ framework = "net$channel-windows"; result = 'passed'; sdk = $sdkVersions[$channel] }
        $destination = Join-Path $share "artifacts\net$channel-windows"
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        Copy-Item "$work\bin\Release\net$channel-windows\*" $destination -Recurse -Force
    }

    foreach ($framework in @('v4.8', 'v4.8.1')) {
        $work = Join-Path $setupRoot "smoke-netfx$framework"
        New-Item -ItemType Directory -Force -Path $work | Out-Null
        @'
using System;
using System.Windows;
namespace WpfFrameworkSmoke {
    public class App : Application {
        [STAThread]
        public static void Main() {
            new App().Run(new Window { Title = "WPF build ready", Width = 400, Height = 240 });
        }
    }
}
'@ | Set-Content (Join-Path $work 'App.cs') -Encoding UTF8
        @"
<Project ToolsVersion="Current" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFrameworkVersion>$framework</TargetFrameworkVersion>
    <AssemblyName>WpfFrameworkSmoke</AssemblyName>
    <OutputPath>bin\Release\</OutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="System" /><Reference Include="System.Core" />
    <Reference Include="WindowsBase" /><Reference Include="PresentationCore" /><Reference Include="PresentationFramework" />
    <Compile Include="App.cs" />
  </ItemGroup>
  <Import Project="`$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
</Project>
"@ | Set-Content (Join-Path $work 'WpfFrameworkSmoke.csproj') -Encoding UTF8
        & $msbuild (Join-Path $work 'WpfFrameworkSmoke.csproj') /nologo /p:Configuration=Release /verbosity:minimal
        if ($LASTEXITCODE -ne 0) { throw "MSBuild WPF $framework zwrocil kod $LASTEXITCODE." }
        $checks += @{ framework = $framework; result = 'passed'; tool = 'MSBuild' }
        $destination = Join-Path $share "artifacts\netfx-$framework"
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        Copy-Item "$work\bin\Release\*" $destination -Recurse -Force
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'WPF Developer PowerShell.lnk'))
    $shortcut.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $shortcut.Arguments = '-NoExit -NoProfile -ExecutionPolicy Bypass -File C:\OEM\developer-shell.ps1'
    $shortcut.WorkingDirectory = $env:USERPROFILE
    $shortcut.Save()

    $details = @{ checks = $checks; msbuild = $msbuild; rebootRequired = [bool]$script:rebootRequired }
    Write-SetupStatus 'ready' 'WPF gotowe. Wszystkie cztery kompilacje zakonczone powodzeniem.' $details
} catch {
    Write-SetupStatus 'failed' $_.Exception.Message
    Write-Error $_
    exit 1
} finally {
    Stop-Transcript
    if (Test-Path (Join-Path $share 'status')) {
        Copy-Item (Join-Path $setupRoot 'setup.log') (Join-Path $share 'status\setup.log') -Force -ErrorAction SilentlyContinue
    }
}
