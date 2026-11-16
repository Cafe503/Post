using Postinstall.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Postinstall.Services
{
    public sealed class OperationResult
    {
        public InstalledSnapshot Snapshot { get; set; }
        public string Message { get; set; }
        public bool RestartRequired { get; set; }
    }

    public sealed class InstallationEngine : IDisposable
    {
        public SessionLog Log { get; }
        public InstalledInventory Inventory { get; }
        public WingetService Winget { get; }
        private readonly ProcessRunner runner;
        private readonly Downloads downloads;
        private readonly DirectInstallers direct;
        public InstallationEngine(SessionLog log)
        {
            Log = log; runner = new ProcessRunner(log); downloads = new Downloads(log);
            Inventory = new InstalledInventory(runner, log);
            Winget = new WingetService(runner, log);
            direct = new DirectInstallers(downloads, runner, log);
        }

        public async Task<OperationResult> InstallAsync(AppDefinition app, CancellationToken token, Action<string> status, string localInstaller = null)
        {
            if (app.Id == "libreoffice") LibreOfficeLanguage.EnsureClosed();
            ProcessResult install;
            status("Instalowanie / aktualizowanie " + app.Name + "…");
            if (localInstaller != null)
            {
                if (app.Id != "coreldraw" && app.Id != "cdburnerxp") throw new InvalidOperationException("Instalator z pliku jest dostępny dla CorelDRAW i CDBurnerXP.");
                await direct.VerifySignatureAsync(localInstaller, app.Id == "coreldraw" ? "Corel|Alludo" : "Canneverbe");
                install = await runner.RunAsync(localInstaller, app.Id == "cdburnerxp" ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-" : "", interactive: app.Id == "coreldraw");
            }
            else if (app.Id == "coreldraw") install = await direct.CorelAsync(token, status);
            else if (app.Id == "cdburnerxp") install = await direct.CDBurnerAsync(token, status);
            else install = await Winget.InstallAsync(app, token);
            CheckInstallResult(install, app.Name);
            status("Sprawdzanie, czy " + app.Name + " został zainstalowany…");
            var snapshot = await WaitForAsync(app, true, app.Id == "coreldraw" ? 900 : 25, status);
            var restart = WingetService.RestartRequired(install.ExitCode);
            if (app.Id == "audacity" && !InstalledInventory.HasAudacityFFmpeg(snapshot))
            {
                var ffmpeg = await direct.FFmpegAsync(token, status);
                CheckInstallResult(ffmpeg, "FFmpeg dla Audacity");
                restart |= WingetService.RestartRequired(ffmpeg.ExitCode);
                snapshot = await Inventory.ScanAsync();
                if (!InstalledInventory.HasAudacityFFmpeg(snapshot))
                    throw new InvalidOperationException("Audacity jest zainstalowany, ale nie potwierdzono biblioteki FFmpeg do MP4. Ponów instalację, aby uzupełnić dodatek.");
            }
            if (app.Id == "audacity") DirectInstallers.ConfigureOwnedFFmpeg();
            if (app.Id == "irfanview")
            {
                await direct.IrfanPolishAsync(snapshot, token, status);
                snapshot = await Inventory.ScanAsync();
                if (!InstalledInventory.HasPolishIrfan(snapshot)) throw new InvalidOperationException("IrfanView jest zainstalowany, ale brakuje polskiego języka.");
            }
            if (app.Id == "libreoffice")
            {
                var folder = InstalledInventory.FindFolder(snapshot, app, @"program\soffice.exe");
                if (!LibreOfficeLanguage.HasPolishResources(folder))
                {
                    status("Uzupełnianie polskiego interfejsu LibreOffice…");
                    var language = await Winget.InstallPolishInterfaceAsync(token);
                    CheckInstallResult(language, "Polski interfejs LibreOffice");
                    restart |= WingetService.RestartRequired(language.ExitCode);
                    snapshot = await Inventory.ScanAsync();
                    folder = InstalledInventory.FindFolder(snapshot, app, @"program\soffice.exe");
                }
                status("Ustawianie polskiego języka interfejsu LibreOffice…");
                LibreOfficeLanguage.ConfigurePolish(folder, Log);
                var version = snapshot.Version(app);
                if (!Regex.IsMatch(version, @"^\d+(\.\d+){2,3}$")) throw new InvalidOperationException("Nie można ustalić wersji LibreOffice do dobrania zgodnej pomocy offline.");
                // Even if an old help pack exists, ensure its version matches the installed suite.
                status("Instalowanie polskiej pomocy offline LibreOffice " + version + "…");
                var help = await Winget.InstallPolishHelpAsync(version, token);
                CheckInstallResult(help, "Polska pomoc offline LibreOffice");
                restart |= WingetService.RestartRequired(help.ExitCode);
                snapshot = await Inventory.ScanAsync();
                if (!InstalledInventory.HasPolishHelp(snapshot)) throw new InvalidOperationException("LibreOffice jest zainstalowany, ale nie potwierdzono polskiej pomocy offline. Ponów instalację, aby uzupełnić dodatek.");
            }
            var message = "Potwierdzono instalację" + (string.IsNullOrWhiteSpace(snapshot.Version(app)) ? "" : " · " + snapshot.Version(app));
            Log.Write(app.Name + ": " + message);
            return new OperationResult { Snapshot = snapshot, Message = message, RestartRequired = restart };
        }

        public async Task<OperationResult> UninstallAsync(AppDefinition app, Action<string> status)
        {
            var before = await Inventory.ScanAsync();
            var main = before.Find(app);
            var libreOfficeFolder = app.Id == "libreoffice"
                ? InstalledInventory.FindFolder(before, app, @"program\soffice.exe") : null;
            var restart = false;
            // Dedicated add-ons are removed before the main program. Other products are untouched.
            if (app.Id == "audacity")
                foreach (var addon in InstalledInventory.FFmpegEntries(before))
                {
                    status("Usuwanie FFmpeg dla Audacity…");
                    restart |= await RemoveRegisteredAsync(addon);
                }
            if (app.Id == "libreoffice")
                foreach (var addon in before.Programs.Where(p => InstalledInventory.IsPolishHelp(p.Name)))
                {
                    status("Usuwanie polskiej pomocy offline LibreOffice…");
                    restart |= await RemoveRegisteredAsync(addon);
                }
            if (main.Count > 0)
            {
                status("Odinstalowywanie " + app.Name + "…");
                var removed = false;
                // IrfanView's uninstaller needs its own folder as the current directory.
                // Use the publisher's documented /silent switch directly.
                if (app.Id != "irfanview" && !string.IsNullOrWhiteSpace(app.WingetId) && Winget.IsReady)
                {
                    var result = await Winget.UninstallAsync(app);
                    if (result.ExitCode == 0 || WingetService.RestartRequired(result.ExitCode))
                    { removed = true; restart |= WingetService.RestartRequired(result.ExitCode); }
                    else Log.Write("WinGet nie usunął programu (" + result.HexCode + "); sprawdzam zarejestrowany deinstalator.");
                }
                if (!removed)
                {
                    // Rescan after WinGet: never run a stale uninstall command a second time.
                    var current = await Inventory.ScanAsync();
                    foreach (var entry in current.Find(app)) restart |= await RemoveRegisteredAsync(entry);
                }
            }
            status("Sprawdzanie odinstalowania " + app.Name + "…");
            var after = await WaitForAsync(app, false, 25, status);
            if (app.Id == "audacity" && InstalledInventory.FFmpegEntries(after).Any())
                throw new InvalidOperationException("Pozostał zainstalowany dodatek FFmpeg dla Audacity.");
            if (app.Id == "libreoffice" && after.Programs.Any(p => InstalledInventory.IsPolishHelp(p.Name)))
                throw new InvalidOperationException("Pozostała zainstalowana polska pomoc LibreOffice.");
            if (app.Id == "libreoffice")
                LibreOfficeLanguage.RemoveSharedDefault(libreOfficeFolder);
            Log.Write(app.Name + ": potwierdzono odinstalowanie.");
            return new OperationResult { Snapshot = after, Message = main.Count == 0 ? "Program nie był zainstalowany" : "Potwierdzono odinstalowanie", RestartRequired = restart };
        }

        private async Task<InstalledSnapshot> WaitForAsync(AppDefinition app, bool installed, int seconds, Action<string> status)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            InstalledSnapshot snapshot;
            do
            {
                snapshot = await Inventory.ScanAsync();
                if (snapshot.IsInstalled(app) == installed) return snapshot;
                await Task.Delay(2000);
            } while (DateTime.UtcNow < deadline);
            throw new InvalidOperationException(installed
                ? "Nie potwierdzono instalacji " + app.Name + ". Instalator mógł zostać anulowany. Sprawdź dziennik i ponów próbę."
                : "Program " + app.Name + " nadal figuruje jako zainstalowany. Deinstalacja mogła zostać anulowana lub wymagać restartu.");
        }

        private async Task<bool> RemoveRegisteredAsync(InstalledProgram entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.AppxFullName))
            {
                var appx = await runner.PowerShellAsync("Remove-AppxPackage -Package " + ProcessRunner.QuotePowerShell(entry.AppxFullName));
                if (appx.ExitCode != 0) throw new InvalidOperationException("Nie udało się usunąć " + entry.Name + ". " + appx.Output.Trim());
                return false;
            }
            var quiet = !string.IsNullOrWhiteSpace(entry.QuietUninstallCommand);
            var command = quiet ? entry.QuietUninstallCommand : entry.UninstallCommand;
            if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("Brak zarejestrowanego deinstalatora: " + entry.Name);
            string executable, arguments;
            ParseCommand(command, out executable, out arguments);
            if (Path.GetFileName(executable).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(executable).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                var guid = Regex.Match(arguments, @"\{[a-fA-F0-9-]{36}\}");
                if (!guid.Success) throw new InvalidOperationException("Nie można ustalić identyfikatora MSI: " + entry.Name);
                executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
                arguments = "/x " + guid.Value + " /qn /norestart"; quiet = true;
            }
            // Inno Setup documents these switches for its uninstaller.
            else if (!quiet && Regex.IsMatch(Path.GetFileName(executable), @"^unins\d*\.exe$", RegexOptions.IgnoreCase))
            { arguments += " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART"; quiet = true; }
            else if (!quiet && Path.GetFileName(executable).Equals("iv_uninstall.exe", StringComparison.OrdinalIgnoreCase))
            { arguments += " /silent"; quiet = true; }
            Log.Write("Deinstalator: " + entry.Name + (quiet ? " (bez okna)" : " (dokończ w oknie producenta)"));
            var workingDirectory = Path.IsPathRooted(executable) ? Path.GetDirectoryName(executable) : null;
            var result = await runner.RunAsync(executable, arguments, interactive: !quiet, workingDirectory: workingDirectory);
            if (result.ExitCode != 0 && !WingetService.RestartRequired(result.ExitCode))
                throw new InvalidOperationException("Deinstalator " + entry.Name + " zwrócił błąd " + result.HexCode + ".");
            return WingetService.RestartRequired(result.ExitCode);
        }

        public static void ParseCommand(string command, out string executable, out string arguments)
        {
            command = Environment.ExpandEnvironmentVariables(command.Trim());
            if (command.StartsWith("\""))
            {
                var end = command.IndexOf('"', 1);
                if (end <= 1) throw new InvalidOperationException("Niepoprawna ścieżka deinstalatora.");
                executable = command.Substring(1, end - 1); arguments = command.Substring(end + 1).Trim();
            }
            else
            {
                var end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (end >= 0) { executable = command.Substring(0, end + 4); arguments = command.Substring(end + 4).Trim(); }
                else
                {
                    var space = command.IndexOf(' ');
                    executable = space < 0 ? command : command.Substring(0, space);
                    arguments = space < 0 ? "" : command.Substring(space + 1);
                }
            }
            if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("Pusta ścieżka deinstalatora.");
            // Deliberately do not invoke cmd.exe or interpret &, pipes, or command substitution.
        }

        private static void CheckInstallResult(ProcessResult result, string name)
        {
            if (!WingetService.InstallSucceeded(result.ExitCode))
                throw new InvalidOperationException(name + ": instalator zwrócił błąd " + result.HexCode + ". Szczegóły są w dzienniku.");
        }

        public void Dispose() => downloads.Dispose();
    }
}
