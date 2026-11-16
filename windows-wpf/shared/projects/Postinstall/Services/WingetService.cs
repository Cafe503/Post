using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Postinstall.Models;

namespace Postinstall.Services
{
    public sealed class WingetService
    {
        private readonly ProcessRunner runner;
        private readonly SessionLog log;
        public string Executable { get; private set; }
        public string Version { get; private set; }
        public bool IsReady => !string.IsNullOrWhiteSpace(Executable);
        public WingetService(ProcessRunner runner, SessionLog log) { this.runner = runner; this.log = log; }

        public async Task<bool> ProbeAsync()
        {
            Executable = null; Version = null;
            var query = await runner.PowerShellAsync("$p=Get-AppxPackage -Name Microsoft.DesktopAppInstaller | Where-Object { Test-Path (Join-Path $_.InstallLocation 'winget.exe') } | Select-Object -First 1; if ($p) { Join-Path $p.InstallLocation 'winget.exe' }", logOutput: false);
            if (query.ExitCode != 0) { log.Write(query.Output); return false; }
            var path = query.Output.Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            try
            {
                var result = await runner.RunAsync(path, "--version", timeoutSeconds: 30, logOutput: false);
                if (result.ExitCode != 0) return false;
                System.Version version;
                if (!System.Version.TryParse(result.Output.Trim().TrimStart('v'), out version) || version < new System.Version(1, 8)) return false;
                Executable = path; Version = result.Output.Trim(); return true;
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }

        public async Task EnsureAsync(CancellationToken token, Action<string> status)
        {
            token.ThrowIfCancellationRequested();
            if (!IsReady && !await ProbeAsync())
            {
                status("Przygotowywanie narzędzia do instalacji…");
                log.Write("Przygotowywanie oficjalnego Microsoft App Installer (WinGet).");
                string script;
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Postinstall.Resources.Bootstrap-WinGet.ps1"))
                using (var reader = new StreamReader(stream)) script = reader.ReadToEnd();
                var result = await runner.PowerShellAsync(script, 1800);
                if (result.ExitCode != 0 || !await ProbeAsync())
                    throw new InvalidOperationException("Nie udało się przygotować WinGet. Sprawdź połączenie i dziennik. Możesz zainstalować „Instalator aplikacji” Microsoft z Microsoft Store i ponowić próbę.");
            }
            token.ThrowIfCancellationRequested();
            status("Odświeżanie katalogu najnowszych wersji…");
            var update = await runner.RunAsync(Executable, "source update --name winget --disable-interactivity", timeoutSeconds: 180);
            if (update.ExitCode != 0)
                throw new InvalidOperationException("Nie udało się odświeżyć katalogu programów (" + update.HexCode + "). Sprawdź internet i ponów próbę.");
            log.Write("Katalog WinGet odświeżony; instalowane będą najnowsze dostępne stabilne wersje.");
        }

        public async Task<ProcessResult> InstallAsync(AppDefinition app, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var args = "install --id " + ProcessRunner.Quote(app.WingetId) + " --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
            if (app.Id == "firefox") args += " --locale pl-PL";
            if (app.Id == "libreoffice") args += " --custom " + ProcessRunner.Quote("UI_LANGS=pl RebootYesNo=No");
            log.Write(app.Name + ": instalacja / aktualizacja z katalogu WinGet.");
            // Cancellation is checked before starting, not while MSI is making changes.
            return await runner.RunAsync(Executable, args);
        }

        public Task<ProcessResult> InstallPolishHelpAsync(string version, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return runner.RunAsync(Executable, "install --id TheDocumentFoundation.LibreOffice.HelpPack --exact --source winget --version " +
                ProcessRunner.Quote(version) + " --locale pl-PL --silent --accept-package-agreements --accept-source-agreements --disable-interactivity");
        }

        public Task<ProcessResult> InstallPolishInterfaceAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            log.Write("LibreOffice: uzupełnienie polskiej funkcji MSI w istniejącej instalacji.");
            return runner.RunAsync(Executable, "install --id TheDocumentFoundation.LibreOffice --exact --source winget --force --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --custom " +
                ProcessRunner.Quote("ADDLOCAL=gm_Langpack_r_pl UI_LANGS=pl RebootYesNo=No"));
        }

        public Task<ProcessResult> UninstallAsync(AppDefinition app)
        {
            log.Write(app.Name + ": odinstalowanie przez WinGet.");
            return runner.RunAsync(Executable, "uninstall --id " + ProcessRunner.Quote(app.WingetId) + " --exact --source winget --silent --accept-source-agreements --disable-interactivity");
        }

        public static bool InstallSucceeded(int code) => code == 0 || code == 3010 || code == 1641 ||
            unchecked((uint)code) == 0x8A15002B || unchecked((uint)code) == 0x8A150061 ||
            unchecked((uint)code) == 0x8A15010D || unchecked((uint)code) == 0x8A150109;

        public static bool RestartRequired(int code) => code == 3010 || code == 1641 || unchecked((uint)code) == 0x8A150109;
    }
}
