using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Postinstall.Models
{
    public class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Changed([CallerMemberName] string name = null)
        { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value; Changed(name); return true;
        }
    }

    public sealed class AppDefinition
    {
        public string Id { get; }
        public string Name { get; }
        public string Category { get; }
        public string Description { get; }
        public string Monogram { get; }
        public string Color { get; }
        public string IconBackground { get; }
        public string WingetId { get; }
        public string Website { get; }
        public string License { get; }
        private readonly Regex installedName;

        public AppDefinition(string id, string name, string category, string description,
            string monogram, string color, string background, string wingetId, string website,
            string pattern, string license = "Bezpłatny")
        {
            Id = id; Name = name; Category = category; Description = description;
            Monogram = monogram; Color = color; IconBackground = background;
            WingetId = wingetId; Website = website; License = license;
            installedName = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public bool Matches(string displayName) => installedName.IsMatch(displayName ?? "");
    }

    public static class Catalog
    {
        public static IReadOnlyList<AppDefinition> Create() => new[]
        {
            new AppDefinition("synology-chat", "Synology Chat", "Praca", "Komunikator do rozmów i współpracy przez serwer Synology", "SC", "#218352", "#E8F6ED", "Synology.ChatClient", "https://www.synology.com/pl-pl/dsm/feature/chat", @"^Synology Chat(?: Client)?(?:\s+\d[\d.\-]*(?:\s+\((?:x64|x86|64-bit|32-bit)\))?)?$"),
            new AppDefinition("synology-drive", "Synology Drive", "Praca", "Synchronizacja plików i kopie zapasowe z Synology NAS", "SD", "#2866CA", "#EAF1FF", "Synology.DriveClient", "https://www.synology.com/pl-pl/dsm/feature/drive", @"^Synology Drive(?: Client)?(?:\s+\d[\d.\-]*(?:\s+\((?:x64|x86|64-bit|32-bit)\))?)?$"),
            new AppDefinition("7zip", "7-Zip", "Narzędzia", "Archiwizacja i rozpakowywanie plików", "7z", "#374151", "#EEF0F3", "7zip.7zip", "https://www.7-zip.org/", @"^7-Zip\s+\d"),
            new AppDefinition("chrome", "Google Chrome", "Internet", "Przeglądarka internetowa Google", "Ch", "#218352", "#E8F6ED", "Google.Chrome", "https://www.google.com/chrome/", @"^Google Chrome$")
        };

        public static AppDefinition Find(string id) => Create().Single(a => a.Id == id);
    }

    public sealed class ProgramItem : Observable
    {
        public AppDefinition Definition { get; }
        public ProgramItem(AppDefinition definition) { Definition = definition; }
        private bool selected, installed;
        private string version = "", status = "Sprawdzanie…", detail = "", tone = "neutral";
        public bool IsSelected { get => selected; set => Set(ref selected, value); }
        public bool IsInstalled { get => installed; set { if (Set(ref installed, value)) Changed(nameof(InstallLabel)); } }
        public string Version { get => version; set => Set(ref version, value); }
        public string Status { get => status; set => Set(ref status, value); }
        public string Detail { get => detail; set => Set(ref detail, value); }
        public string Tone { get => tone; set => Set(ref tone, value); }
        public string InstallLabel => IsInstalled ? "Aktualizuj" : "Zainstaluj";
    }
}
