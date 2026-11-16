using Postinstall.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Postinstall.Services
{
    public sealed class DirectInstallers
    {
        private readonly Downloads downloads;
        private readonly ProcessRunner runner;
        private readonly SessionLog log;
        public DirectInstallers(Downloads downloads, ProcessRunner runner, SessionLog log)
        { this.downloads = downloads; this.runner = runner; this.log = log; }

        public static string NewWorkFolder()
        {
            var folder = Path.Combine(Path.GetTempPath(), "Postinstall-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder); return folder;
        }
        public static void Clean(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public async Task<ProcessResult> CorelAsync(CancellationToken token, Action<string> status)
        {
            var work = NewWorkFolder();
            try
            {
                var path = await PrepareCorelAsync(work, token, status);
                token.ThrowIfCancellationRequested();
                status("Dokończ instalację w oknie CorelDRAW. Po zamknięciu kreatora sprawdzę wynik.");
                log.Write("Uruchomiono oficjalny kreator CorelDRAW. Kreator może wymagać konta i licencji / aktywacji wersji próbnej.");
                return await runner.RunAsync(path, "", interactive: true, timeoutSeconds: 7200);
            }
            finally { Clean(work); }
        }

        public async Task<string> PrepareCorelAsync(string work, CancellationToken token, Action<string> status)
        {
            status("Wyszukiwanie aktualnego instalatora CorelDRAW…");
            var html = await downloads.TextAsync("https://www.coreldraw.com/en/free-trials/", token);
            var links = Regex.Matches(html, @"(?:href|src)\s*=\s*[""'](?<url>[^""']+\.exe\s*)[""']", RegexOptions.IgnoreCase)
                .Cast<Match>().Select(m => WebUtility.HtmlDecode(m.Groups["url"].Value.Trim()))
                .Where(u => u.IndexOf("GraphicsSuite", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (links.Count == 0) throw new InvalidOperationException("Strona CorelDRAW nie udostępniła instalatora Windows. Otwórz stronę producenta przy programie i sprawdź dostępność.");
            var url = Downloads.RequireHost(links.First(), "corel.com", "coreldraw.com");
            var path = Path.Combine(work, "CorelDRAW-Setup.exe");
            await downloads.FileAsync(url, path, token, status);
            status("Sprawdzanie podpisu instalatora CorelDRAW…");
            await VerifySignatureAsync(path, "Corel|Alludo");
            return path;
        }

        public async Task<ProcessResult> CDBurnerAsync(CancellationToken token, Action<string> status)
        {
            status("Sprawdzanie ostatniej dostępnej wersji CDBurnerXP…");
            var html = await downloads.TextAsync("https://www.fosshub.com/CDBurnerXP.html", token);
            var match = Regex.Match(html, @"var\s+settings\s*=\s*(?<json>\{[^\r\n]+\})", RegexOptions.IgnoreCase);
            if (!match.Success) throw new InvalidOperationException("Nie można odczytać katalogu plików CDBurnerXP.");
            var settings = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(match.Groups["json"].Value);
            var pool = (Dictionary<string, object>)settings["pool"];
            var files = ((System.Collections.IEnumerable)pool["f"]).Cast<object>().OfType<Dictionary<string, object>>();
            var pattern = Environment.Is64BitOperatingSystem ? @"^cdbxp_setup_[\d.]+_x64_minimal\.exe$" : @"^cdbxp_setup_[\d.]+_minimal\.exe$";
            var file = files.FirstOrDefault(f => Regex.IsMatch(Convert.ToString(f["n"]), pattern));
            if (file == null) throw new InvalidOperationException("Nie znaleziono instalatora CDBurnerXP dla tego systemu.");
            var name = Convert.ToString(file["n"]);
            var hash = Convert.ToString(((Dictionary<string, object>)file["hash"])["sha256"]);
            if (!Regex.IsMatch(hash, @"^[a-fA-F0-9]{64}$")) throw new InvalidOperationException("Źródło nie podało poprawnej sumy kontrolnej CDBurnerXP.");
            var work = NewWorkFolder();
            try
            {
                var path = Path.Combine(work, name);
                // The publisher stopped distribution. The archived mirror is usable only
                // with the original publisher-distribution SHA-256 and Authenticode signature.
                Exception downloadError = null;
                var urls = new List<string>();
                try
                {
                    var mirrorPage = await downloads.TextAsync("https://www.free-codecs.com/cdburnerxp_pro_download.htm", token);
                    var mirror = Regex.Matches(mirrorPage, @"""target""\s*:\s*""(?<url>https://[^""]+\.exe)""")
                        .Cast<Match>().Select(m => m.Groups["url"].Value)
                        .FirstOrDefault(u => u.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
                    if (mirror != null) urls.Add(Downloads.RequireHost(mirror, "freecodecs.net", "free-codecs.com"));
                }
                catch (HttpRequestException e) { log.Write("Lustro archiwum: " + e.Message); }
                urls.AddRange(new[] { "master.dl.sourceforge.net", "netix.dl.sourceforge.net", "downloads.sourceforge.net" }
                    .Select(host => "https://" + host + "/project/cdburnerxp/" + name));
                foreach (var url in urls)
                {
                    try
                    {
                        await downloads.FileAsync(url, path, token, status, hash);
                        downloadError = null; break;
                    }
                    catch (Exception e) when (e is HttpRequestException || e is IOException)
                    { downloadError = e; log.Write("Lustro CDBurnerXP jest niedostępne: " + e.Message); }
                }
                if (downloadError != null) throw new InvalidOperationException("Nie można pobrać archiwalnego instalatora CDBurnerXP. Sprawdź internet lub użyj opcji „Instalator z pliku”.", downloadError);
                await VerifySignatureAsync(path, "Canneverbe");
                status("Instalowanie CDBurnerXP…");
                token.ThrowIfCancellationRequested();
                return await runner.RunAsync(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-");
            }
            finally { Clean(work); }
        }

        public async Task<ProcessResult> FFmpegAsync(CancellationToken token, Action<string> status)
        {
            if (Environment.Is64BitOperatingSystem)
                return await FFmpegSharedAsync(token, status);
            status("Pobieranie FFmpeg do importu MP4 w Audacity…");
            var html = await downloads.TextAsync("https://lame.buanzo.org/ffmpeg.php", token);
            var suffix = Environment.Is64BitOperatingSystem ? "x86_64" : "x86";
            var pattern = @"(?:href)\s*=\s*[""'](?<url>[^""']*FFmpeg_[\d.]+_for_Audacity_on_Windows_" + suffix + @"\.exe)[""']";
            var link = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (!link.Success) throw new InvalidOperationException("Nie znaleziono zgodnego dodatku FFmpeg dla Audacity.");
            var url = new Uri(new Uri("https://lame.buanzo.org/ffmpeg.php"), WebUtility.HtmlDecode(link.Groups["url"].Value)).AbsoluteUri;
            Downloads.RequireHost(url, "lame.buanzo.org");
            var work = NewWorkFolder();
            try
            {
                var path = Path.Combine(work, "FFmpeg-for-Audacity.exe");
                await downloads.FileAsync(url, path, token, status);
                RequireExecutable(path);
                token.ThrowIfCancellationRequested();
                status("Instalowanie dodatku FFmpeg…");
                log.Write("FFmpeg pochodzi ze źródła zalecanego przez dokumentację Audacity: lame.buanzo.org.");
                return await runner.RunAsync(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-");
            }
            finally { Clean(work); }
        }

        private async Task<ProcessResult> FFmpegSharedAsync(CancellationToken token, Action<string> status)
        {
            status("Pobieranie zgodnej biblioteki FFmpeg do MP4…");
            var release = new JavaScriptSerializer { MaxJsonLength = 4000000 }.Deserialize<Dictionary<string, object>>(
                await downloads.TextAsync("https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest", token));
            // Audacity 3.7+ supports libavformat 62 (FFmpeg 8). Never use master builds.
            var assets = ((System.Collections.IEnumerable)release["assets"]).Cast<object>().OfType<Dictionary<string, object>>();
            var asset = assets.FirstOrDefault(a => Regex.IsMatch(Convert.ToString(a["name"]),
                @"^ffmpeg-n8\.1-latest-win64-lgpl-shared.*\.zip$", RegexOptions.IgnoreCase));
            if (asset == null) throw new InvalidOperationException("Nie znaleziono zgodnej stabilnej biblioteki FFmpeg 8.1 dla Audacity.");
            var digest = Convert.ToString(asset.ContainsKey("digest") ? asset["digest"] : "");
            if (!Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$"))
                throw new InvalidOperationException("Źródło FFmpeg nie podało sumy SHA-256.");
            var url = Downloads.RequireHost(Convert.ToString(asset["browser_download_url"]), "github.com");
            if (!url.StartsWith("https://github.com/BtbN/FFmpeg-Builds/releases/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Nieznane źródło FFmpeg.");
            var work = NewWorkFolder();
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FFmpeg for Audacity");
            try
            {
                var zip = Path.Combine(work, "FFmpeg-shared.zip");
                await downloads.FileAsync(url, zip, token, status, digest.Substring(7));
                if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any() && !File.Exists(Path.Combine(folder, ".postinstall-owned")))
                    throw new InvalidOperationException("Katalog FFmpeg dla Audacity zawiera wcześniejsze pliki. Usuń poprzednią instalację FFmpeg i ponów próbę.");
                status("Instalowanie bibliotek FFmpeg dla Audacity…");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, ".postinstall-owned"), "Postinstall FFmpeg shared 8.1");
                using (var archive = ZipFile.OpenRead(zip))
                {
                    foreach (var entry in archive.Entries.Where(e =>
                        (e.FullName.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase) >= 0 && e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ||
                        e.Name.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)))
                        entry.ExtractToFile(Path.Combine(folder, entry.Name), true);
                }
                if (!File.Exists(Path.Combine(folder, "avformat-62.dll")))
                    throw new InvalidOperationException("W archiwum FFmpeg brakuje avformat-62.dll.");
                ConfigureOwnedFFmpeg();
                log.Write("Zainstalowano komplet bibliotek FFmpeg 8.1 (avformat-62) ze źródła wskazanego przez Audacity.");
                return new ProcessResult { ExitCode = 0, Output = "FFmpeg 8.1 installed" };
            }
            finally { Clean(work); }
        }

        public static void ConfigureOwnedFFmpeg()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FFmpeg for Audacity");
            if (!File.Exists(Path.Combine(folder, ".postinstall-owned")) || !File.Exists(Path.Combine(folder, "avformat-62.dll"))) return;
            using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\FFmpeg for Audacity"))
                key.SetValue("InstallPath", folder);
            var uninstall = "$ErrorActionPreference='Stop'; if (-not (Test-Path -LiteralPath " +
                ProcessRunner.QuotePowerShell(Path.Combine(folder, ".postinstall-owned")) + ")) { throw 'Nieznany katalog FFmpeg' }; " +
                "Remove-Item -LiteralPath " + ProcessRunner.QuotePowerShell(folder) + " -Recurse -Force; " +
                @"$k='HKLM:\SOFTWARE\FFmpeg for Audacity'; if ((Get-ItemProperty -LiteralPath $k -Name InstallPath -ErrorAction SilentlyContinue).InstallPath -eq " +
                ProcessRunner.QuotePowerShell(folder) + ") { Remove-ItemProperty -LiteralPath $k -Name InstallPath }; " +
                @"Remove-Item -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PostinstallAudacityFFmpeg' -Force";
            var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            var command = ProcessRunner.Quote(ps) + " -NoProfile -NonInteractive -EncodedCommand " +
                Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(uninstall));
            using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PostinstallAudacityFFmpeg"))
            {
                key.SetValue("DisplayName", "FFmpeg 8.1 for Audacity (Postinstall)");
                key.SetValue("DisplayVersion", "8.1");
                key.SetValue("Publisher", "BtbN / FFmpeg");
                key.SetValue("InstallLocation", folder);
                key.SetValue("UninstallString", command);
                key.SetValue("QuietUninstallString", command);
                key.SetValue("NoModify", 1);
                key.SetValue("NoRepair", 1);
            }
        }

        public async Task IrfanPolishAsync(InstalledSnapshot snapshot, CancellationToken token, Action<string> status)
        {
            var folder = InstalledInventory.FindFolder(snapshot, Catalog.Find("irfanview"), "i_view64.exe", "i_view32.exe");
            if (folder == null) throw new InvalidOperationException("Nie znaleziono katalogu IrfanView.");
            status("Pobieranie polskiego języka i pomocy IrfanView…");
            var html = await downloads.TextAsync("https://www.irfanview.com/languages.htm", token);
            var link = Regex.Match(html, @"href\s*=\s*[""'](?<url>[^""']*irfanview_lang_(?:polski|polish)\.zip)[""']", RegexOptions.IgnoreCase);
            if (!link.Success) throw new InvalidOperationException("Strona IrfanView nie udostępniła polskiego pakietu językowego.");
            var url = Downloads.RequireHost(WebUtility.HtmlDecode(link.Groups["url"].Value), "irfanview.net", "irfanview.com", "irfanview.info");
            var work = NewWorkFolder();
            try
            {
                var path = Path.Combine(work, "polski.zip");
                await downloads.FileAsync(url, path, token, status);
                status("Dodawanie języka polskiego do IrfanView…");
                var destination = Path.Combine(folder, "Languages");
                Directory.CreateDirectory(destination);
                using (var archive = ZipFile.OpenRead(path))
                {
                    foreach (var entry in archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
                    {
                        var fullPath = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        if (!fullPath.StartsWith(destination.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Pakiet językowy zawiera niepoprawną ścieżkę.");
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                        entry.ExtractToFile(fullPath, true);
                    }
                }
                if (!File.Exists(Path.Combine(destination, "Polski.dll"))) throw new InvalidOperationException("Nie odnaleziono polskiego pliku językowego IrfanView.");
                // Set only the language fields, preserving all existing user preferences.
                var configName = File.Exists(Path.Combine(folder, "i_view64.exe")) ? "i_view64.ini" : "i_view32.ini";
                var personal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IrfanView");
                Directory.CreateDirectory(personal);
                foreach (var config in new[] { Path.Combine(folder, configName), Path.Combine(personal, configName) })
                {
                    NativeIni.Write("Language", "DLL", "Polski.dll", config);
                    NativeIni.Write("Language", "Lang", "Polski", config);
                }
                log.Write("Zainstalowano polski język i pomoc IrfanView.");
            }
            finally { Clean(work); }
        }

        public async Task VerifySignatureAsync(string path, string publisherPattern)
        {
            RequireExecutable(path);
            var result = await runner.PowerShellAsync("$s=Get-AuthenticodeSignature -LiteralPath " + ProcessRunner.QuotePowerShell(path) +
                "; if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch " + ProcessRunner.QuotePowerShell(publisherPattern) +
                ") { throw ('Niepoprawny podpis instalatora: ' + $s.Status) }; Write-Output ('Podpis zweryfikowany: ' + $s.SignerCertificate.Subject)");
            if (result.ExitCode != 0) throw new InvalidOperationException("Nie udało się zweryfikować podpisu producenta. Instalator nie został uruchomiony.");
        }

        public static void RequireExecutable(string path)
        {
            using (var stream = File.OpenRead(path))
                if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidOperationException("Pobrany plik nie jest instalatorem Windows.");
        }
    }

    internal static class NativeIni
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool WritePrivateProfileString(string section, string key, string value, string path);
        public static void Write(string section, string key, string value, string path)
        { if (!WritePrivateProfileString(section, key, value, path)) throw new IOException("Nie udało się ustawić języka IrfanView."); }
    }
}
