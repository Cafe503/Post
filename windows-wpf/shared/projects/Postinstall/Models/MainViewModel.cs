using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;

namespace Postinstall.Models
{
    public sealed class MainViewModel : Observable
    {
        public ObservableCollection<ProgramItem> Programs { get; }
        public ICollectionView VisiblePrograms { get; }
        public string[] Categories { get; }
        public string CatalogLabel => "Wyświetlono " + VisibleCount + " z " + Programs.Count + " programów";
        private bool busy = true, stopping, indeterminate;
        private string search = "", category = "Wszystkie", filter = "Wszystkie", activity = "Sprawdzanie zainstalowanych programów…", operationDetail = "", systemStatus = "Sprawdzanie systemu…", queueLabel = "Brak aktywnej kolejki", logText = "";
        private double progress;
        public bool Busy { get => busy; set { if (Set(ref busy, value)) { Changed(nameof(CanOperate)); Changed(nameof(CanInstallSelected)); Changed(nameof(CanUninstallSelected)); Changed(nameof(CanStop)); } } }
        public bool CanOperate => !Busy;
        public bool Stopping { get => stopping; set { if (Set(ref stopping, value)) Changed(nameof(CanStop)); } }
        public bool CanStop => Busy && !Stopping && QueueActive;
        private bool queueActive;
        public bool QueueActive { get => queueActive; set { if (Set(ref queueActive, value)) Changed(nameof(CanStop)); } }
        public bool Indeterminate { get => indeterminate; set => Set(ref indeterminate, value); }
        public double Progress { get => progress; set => Set(ref progress, value); }
        public string Search { get => search; set { if (Set(ref search, value)) RefreshFilter(); } }
        public string Category { get => category; set { if (Set(ref category, value)) RefreshFilter(); } }
        public string Filter { get => filter; set { if (Set(ref filter, value)) RefreshFilter(); } }
        public string Activity { get => activity; set => Set(ref activity, value); }
        public string OperationDetail { get => operationDetail; set => Set(ref operationDetail, value); }
        public string SystemStatus { get => systemStatus; set => Set(ref systemStatus, value); }
        public string QueueLabel { get => queueLabel; set => Set(ref queueLabel, value); }
        public string LogText { get => logText; set => Set(ref logText, value); }
        public int SelectedCount => Programs.Count(p => p.IsSelected);
        public int InstalledCount => Programs.Count(p => p.IsInstalled);
        public int VisibleCount => VisiblePrograms.Cast<object>().Count();
        public bool Empty => VisibleCount == 0;
        public bool CanInstallSelected => CanOperate && SelectedCount > 0;
        public bool CanUninstallSelected => CanOperate && Programs.Any(p => p.IsSelected && p.IsInstalled);
        public string SelectionLabel => "Zaznaczono: " + SelectedCount;
        public MainViewModel()
        {
            Programs = new ObservableCollection<ProgramItem>(Catalog.Create().Select(a => new ProgramItem(a)));
            Categories = new[] { "Wszystkie" }.Concat(Programs.Select(p => p.Definition.Category).Distinct()).ToArray();
            VisiblePrograms = CollectionViewSource.GetDefaultView(Programs);
            VisiblePrograms.Filter = Matches;
            foreach (var item in Programs) item.PropertyChanged += ItemChanged;
        }
        private bool Matches(object obj)
        {
            var item = (ProgramItem)obj;
            var query = Search.Trim();
            return (Category == "Wszystkie" || item.Definition.Category == Category) &&
                (Filter == "Wszystkie" || (Filter == "Zainstalowane" && item.IsInstalled) || (Filter == "Do instalacji" && !item.IsInstalled) || (Filter == "Zaznaczone" && item.IsSelected)) &&
                (query.Length == 0 || (item.Definition.Name + " " + item.Definition.Description).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        public void RefreshFilter() { VisiblePrograms.Refresh(); Changed(nameof(VisibleCount)); Changed(nameof(CatalogLabel)); Changed(nameof(Empty)); }
        private void ItemChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProgramItem.IsSelected) || e.PropertyName == nameof(ProgramItem.IsInstalled))
            {
                Changed(nameof(SelectedCount)); Changed(nameof(InstalledCount)); Changed(nameof(SelectionLabel)); Changed(nameof(CanInstallSelected)); Changed(nameof(CanUninstallSelected));
                RefreshFilter();
            }
        }
    }
}
