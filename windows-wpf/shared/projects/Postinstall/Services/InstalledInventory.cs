using Microsoft.Win32;
using Postinstall.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Postinstall.Services
{
    public sealed class InstalledProgram
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string Location { get; set; }
        public string UninstallCommand { get; set; }
        public string QuietUninstallCommand { get; set; }
        public string RegistryKey { get; set; }
        public string DisplayIcon { get; set; }
        public string AppxFullName { get; set; }
    }

    public sealed class InstalledSnapshot
    {
        public IReadOnlyList<InstalledProgram> Programs { get; set; }
        public List<InstalledProgram> Find(AppDefinition app) => Programs.Where(p => app.Matches(p.Name)).ToList();
        public bool IsInstalled(AppDefinition app) => Find(app).Count > 0;
        public string Version(AppDefinition app) => Find(app).Select(p => p.Version).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
    }

    public sealed class InstalledInventory
    {
        private readonly ProcessRunner runner;
        private readonly SessionLog log;
        public InstalledInventory(ProcessRunner runner, SessionLog log) { this.runner = runner; this.log = log; }

        public async Task<InstalledSnapshot> ScanAsync()
        {
            var list = await Task.Run(ReadRegistry);
            return new InstalledSnapshot { Programs = list };
        }

        private List<InstalledProgram> ReadRegistry()
        {
            var result = new List<InstalledProgram>();
            var views = Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 };
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (var view in views)
                    using (var root = RegistryKey.OpenBaseKey(hive, view))
                    using (var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (uninstall == null) continue;
                        foreach (var name in uninstall.GetSubKeyNames())
                            try
                            {
                                using (var key = uninstall.OpenSubKey(name))
                                {
                                    if (key == null) continue;
                                    var display = Convert.ToString(key.GetValue("DisplayName"));
                                    if (string.IsNullOrWhiteSpace(display)) continue;
                                    result.Add(new InstalledProgram
                                    {
                                        Name = display, Version = Convert.ToString(key.GetValue("DisplayVersion")),
                                        Location = Convert.ToString(key.GetValue("InstallLocation")),
                                        DisplayIcon = Convert.ToString(key.GetValue("DisplayIcon")),
                                        UninstallCommand = Convert.ToString(key.GetValue("UninstallString")),
                                        QuietUninstallCommand = Convert.ToString(key.GetValue("QuietUninstallString")),
                                        RegistryKey = name
                                    });
                                }
                            }
                            catch (Exception e) when (e is SecurityException || e is UnauthorizedAccessException || e is IOException)
                            { throw new InvalidOperationException("Nie można odczytać listy instalacji z rejestru.", e); }
                    }
            return result.GroupBy(p => p.Name + "|" + p.Version + "|" + p.UninstallCommand, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        }

        public static string FindFolder(InstalledSnapshot snapshot, AppDefinition app, params string[] executableNames)
        {
            foreach (var program in snapshot.Find(app))
            {
                if (!string.IsNullOrEmpty(program.Location) && Directory.Exists(program.Location)) return program.Location.TrimEnd('\\');
                var icon = (program.DisplayIcon ?? "").Trim();
                if (icon.StartsWith("\"")) icon = icon.Split('"').ElementAtOrDefault(1) ?? "";
                else if (icon.LastIndexOf(',') > 0) icon = icon.Substring(0, icon.LastIndexOf(','));
                if (File.Exists(icon)) return Path.GetDirectoryName(icon);
            }
            var names = app.Id == "irfanview" ? new[] { "IrfanView" } : new[] { "LibreOffice" };
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Distinct())
                foreach (var name in names)
                {
                    var folder = Path.Combine(root, name);
                    if (executableNames.Any(exe => File.Exists(Path.Combine(folder, exe)))) return folder;
                }
            return null;
        }

        public static bool HasPolishIrfan(InstalledSnapshot snapshot)
        {
            var folder = FindFolder(snapshot, Catalog.Find("irfanview"), "i_view64.exe", "i_view32.exe");
            return folder != null && File.Exists(Path.Combine(folder, "Languages", "Polski.dll"));
        }

        public static bool HasPolishHelp(InstalledSnapshot snapshot)
        {
            var folder = FindFolder(snapshot, Catalog.Find("libreoffice"), @"program\soffice.exe");
            if (folder != null && Directory.Exists(Path.Combine(folder, "help", "pl")) && Directory.EnumerateFiles(Path.Combine(folder, "help", "pl"), "*", SearchOption.AllDirectories).Any()) return true;
            return snapshot.Programs.Any(p => IsPolishHelp(p.Name));
        }

        public static bool IsPolishHelp(string name) => name.StartsWith("LibreOffice", StringComparison.OrdinalIgnoreCase) &&
            name.IndexOf("Help", StringComparison.OrdinalIgnoreCase) >= 0 &&
            (name.IndexOf("Polish", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Polski", StringComparison.OrdinalIgnoreCase) >= 0 || name.EndsWith("(pl)", StringComparison.OrdinalIgnoreCase));

        public static List<InstalledProgram> FFmpegEntries(InstalledSnapshot snapshot) => snapshot.Programs.Where(p =>
            p.Name.IndexOf("FFmpeg", StringComparison.OrdinalIgnoreCase) >= 0 && p.Name.IndexOf("Audacity", StringComparison.OrdinalIgnoreCase) >= 0).ToList();

        public static bool HasAudacityFFmpeg(InstalledSnapshot snapshot)
        {
            var folders = FFmpegEntries(snapshot).Select(p => p.Location).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FFmpeg for Audacity"));
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "FFmpeg for Audacity"));
            return folders.Distinct().Any(folder => Directory.Exists(folder) && Directory.EnumerateFiles(folder, "avformat-*.dll", SearchOption.AllDirectories).Any());
        }
    }
}
