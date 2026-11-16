using Microsoft.Win32;
using Postinstall.Models;
using Postinstall.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Postinstall
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel vm = new MainViewModel();
        private readonly SessionLog log = new SessionLog();
        private readonly InstallationEngine engine;
        private bool restartRequired;

        public MainWindow()
        {
            InitializeComponent(); DataContext = vm;
            engine = new InstallationEngine(log);
            log.LineAdded += OnLogLine;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.VisibleCount) || e.PropertyName == nameof(MainViewModel.SelectedCount))
                    Dispatcher.BeginInvoke(new Action(UpdateSelectionHeader), DispatcherPriority.Background);
            };
            MaxHeight = SystemParameters.WorkArea.Height;
            Height = Math.Min(790, SystemParameters.WorkArea.Height - 16);
            Width = Math.Min(1210, SystemParameters.WorkArea.Width - 16);
        }

        private void OnLogLine(string line)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                vm.LogText += line + Environment.NewLine;
                if (vm.LogText.Length > 65000) vm.LogText = vm.LogText.Substring(vm.LogText.Length - 50000);
                LogBox.ScrollToEnd();
            }));
        }

        private async void WindowLoaded(object sender, RoutedEventArgs e)
        {
            log.Write("Postinstall 1.1.0 · .NET Framework 4.8 · " + Environment.OSVersion.VersionString);
            if (Environment.OSVersion.Version.Major < 10 || Environment.OSVersion.Version.Build < 17763)
            {
                MessageBox.Show(this, "Postinstall wymaga Windows 10 w wersji 1809 lub nowszej, albo Windows 11.", "Wymagania systemowe", MessageBoxButton.OK, MessageBoxImage.Information);
                Close(); return;
            }
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            if (vm.QueueActive) return;
            vm.Busy = true; vm.Indeterminate = true; vm.Activity = "Sprawdzanie programów na komputerze…";
            vm.OperationDetail = "Odczytuję zainstalowane wersje programów.";
            try
            {
                var inventoryTask = engine.Inventory.ScanAsync();
                var probeTask = engine.Winget.ProbeAsync();
                await Task.WhenAll(inventoryTask, probeTask);
                ApplySnapshot(await inventoryTask, true);
                vm.SystemStatus = "Windows " + (Environment.OSVersion.Version.Build >= 22000 ? "11" : "10") + " · Administrator · " +
                    (engine.Winget.IsReady ? "Gotowy do instalacji" : "Narzędzie instalacji zostanie przygotowane automatycznie");
                vm.Activity = "Gotowe. Wybierz programy dla siebie.";
                vm.OperationDetail = "Instalacja i aktualizacja pojedynczo lub w jednym zestawie.";
                log.Write("Odświeżono stan " + vm.Programs.Count + " programów. Wykryto " + vm.InstalledCount + " zainstalowanych.");
            }
            catch (Exception e)
            {
                vm.Activity = "Nie udało się odczytać pełnego stanu programów.";
                vm.OperationDetail = e.Message; log.Write(e.ToString());
                foreach (var item in vm.Programs) { item.Status = "Nie sprawdzono"; item.Tone = "warning"; item.Detail = e.Message; }
            }
            finally { vm.Busy = false; vm.Indeterminate = false; vm.Progress = 0; }
        }

        private void ApplySnapshot(InstalledSnapshot snapshot, bool resetStatuses)
        {
            foreach (var item in vm.Programs)
            {
                item.IsInstalled = snapshot.IsInstalled(item.Definition);
                item.Version = snapshot.Version(item.Definition);
                if (!resetStatuses) continue;
                item.Status = item.IsInstalled ? "Zainstalowany" : "Do instalacji";
                item.Tone = item.IsInstalled ? "success" : "neutral";
                item.Detail = item.IsInstalled ? "Wykryto instalację: " + item.Version : "Program nie jest zainstalowany.";
            }
        }

        private async Task RunBatchAsync(IEnumerable<ProgramItem> requested, bool uninstall, string localInstaller = null)
        {
            if (vm.Busy) return;
            var items = requested.Distinct().ToList();
            if (uninstall) items = items.Where(p => p.IsInstalled).ToList();
            if (items.Count == 0)
            {
                MessageBox.Show(this, uninstall ? "Nie wybrano zainstalowanych programów do usunięcia." : "Zaznacz przynajmniej jeden program.", "Postinstall", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var names = string.Join("\n", items.Select(p => "• " + p.Definition.Name));
            var confirmation = uninstall
                ? "Odinstalować poniższe programy?\n\n" + names + "\n\nUstawienia programu mogą zostać usunięte przez deinstalator producenta."
                : "Zainstalować lub zaktualizować poniższe programy?\n\n" + names + "\n\nKontynuując, akceptujesz warunki licencji wybranych programów i katalogu WinGet.";
            if (MessageBox.Show(this, confirmation, uninstall ? "Potwierdź odinstalowanie" : "Potwierdź instalację", MessageBoxButton.YesNo,
                uninstall ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

            vm.Busy = true; vm.QueueActive = true; vm.Stopping = false; vm.Progress = 0;
            var completed = 0; var succeeded = 0; var failed = 0;
            foreach (var item in items) { item.Status = "W kolejce"; item.Tone = "working"; item.Detail = "Oczekiwanie na poprzedni program."; }
            log.Write((uninstall ? "Odinstalowanie" : "Instalacja / aktualizacja") + " — wybrano: " + string.Join(", ", items.Select(p => p.Definition.Name)));
            try
            {
                if (!uninstall && localInstaller == null && items.Any(p => !string.IsNullOrWhiteSpace(p.Definition.WingetId)))
                {
                    vm.Indeterminate = true;
                    await engine.Winget.EnsureAsync(CancellationToken.None, text => { vm.Activity = text; vm.OperationDetail = "Przygotowanie wspólnego katalogu programów."; });
                }
                foreach (var item in items)
                {
                    if (vm.Stopping) break;
                    vm.Indeterminate = true;
                    vm.QueueLabel = "Program " + (completed + 1) + " z " + items.Count;
                    vm.Activity = (uninstall ? "Odinstalowywanie: " : "Instalacja / aktualizacja: ") + item.Definition.Name;
                    item.Status = uninstall ? "Usuwanie…" : "Instalowanie…"; item.Tone = "working";
                    try
                    {
                        Action<string> status = text => { vm.OperationDetail = text; item.Detail = text; };
                        var result = uninstall
                            ? await engine.UninstallAsync(item.Definition, status)
                            : await engine.InstallAsync(item.Definition, CancellationToken.None, status, localInstaller);
                        ApplySnapshot(result.Snapshot, false);
                        item.Status = uninstall ? "Odinstalowano" : "Zainstalowano";
                        item.Tone = "success"; item.Detail = result.Message;
                        item.IsSelected = false; succeeded++;
                        restartRequired |= result.RestartRequired;
                    }
                    catch (Exception e)
                    {
                        failed++; item.Status = "Błąd"; item.Tone = "error"; item.Detail = e.Message;
                        log.Write(item.Definition.Name + ": " + e);
                        // A partially completed install must remain visible and uninstallable.
                        try { ApplySnapshot(await engine.Inventory.ScanAsync(), false); }
                        catch (Exception scanError) { log.Write("Nie udało się ponownie odczytać instalacji: " + scanError.Message); }
                    }
                    completed++; vm.Progress = completed * 100.0 / items.Count;
                }
            }
            catch (Exception e)
            {
                log.Write("Przygotowanie kolejki: " + e);
                vm.OperationDetail = e.Message;
                foreach (var item in items.Where(p => p.Status == "W kolejce"))
                { item.Status = "Błąd przygotowania"; item.Tone = "error"; item.Detail = e.Message; failed++; }
            }
            finally
            {
                foreach (var item in items.Where(p => p.Status == "W kolejce"))
                { item.Status = "Pominięto"; item.Tone = "neutral"; item.Detail = "Kolejka została zatrzymana przed rozpoczęciem tego programu."; }
                vm.Activity = vm.Stopping ? "Kolejka zatrzymana po zakończeniu bieżącego programu." : failed == 0 ? "Gotowe. Wszystkie operacje zakończone." : "Zakończono. Niektóre operacje wymagają uwagi.";
                var summary = "Powodzenie: " + succeeded + " · Błędy: " + failed + " · Pominięte: " + items.Count(p => p.Status == "Pominięto");
                vm.OperationDetail = summary + (restartRequired ? " · Wymagany restart Windows." : " · Szczegóły w dzienniku i przy programach.");
                vm.QueueLabel = "Ostatnia sesja: " + DateTime.Now.ToString("HH:mm");
                vm.Indeterminate = false; vm.QueueActive = false; vm.Busy = false;
                log.Write(summary + (restartRequired ? " · Wymagany restart Windows." : ""));
            }
        }

        private void UpdateSelectionHeader()
        {
            var items = vm.VisiblePrograms.Cast<ProgramItem>().ToList();
            SelectVisibleBox.IsChecked = items.Count > 0 && items.All(p => p.IsSelected);
        }
        private void SelectVisibleClicked(object sender, RoutedEventArgs e)
        {
            var selected = SelectVisibleBox.IsChecked == true;
            foreach (var item in vm.VisiblePrograms.Cast<ProgramItem>().ToList()) item.IsSelected = selected;
        }
        private void ClearSelectionClicked(object sender, RoutedEventArgs e) { foreach (var item in vm.Programs) item.IsSelected = false; }
        private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
        private async void InstallSelectedClicked(object sender, RoutedEventArgs e) => await RunBatchAsync(vm.Programs.Where(p => p.IsSelected), false);
        private async void UninstallSelectedClicked(object sender, RoutedEventArgs e) => await RunBatchAsync(vm.Programs.Where(p => p.IsSelected), true);
        private async void InstallAllClicked(object sender, RoutedEventArgs e) => await RunBatchAsync(vm.Programs, false);
        private async void UninstallAllClicked(object sender, RoutedEventArgs e) => await RunBatchAsync(vm.Programs, true);
        private async void InstallOneClicked(object sender, RoutedEventArgs e) => await RunBatchAsync(new[] { (ProgramItem)((Button)sender).Tag }, false);
        private async void UninstallOneClicked(object sender, RoutedEventArgs e) => await RunBatchAsync(new[] { (ProgramItem)((Button)sender).Tag }, true);
        private void StopClicked(object sender, RoutedEventArgs e)
        { vm.Stopping = true; vm.Activity = "Kolejka zostanie zatrzymana po bieżącym programie."; log.Write("Zatrzymanie kolejki: bieżący instalator dokończy pracę, kolejne programy zostaną pominięte."); }
        private void LogsClicked(object sender, RoutedEventArgs e) { LogExpander.IsExpanded = true; LogBox.ScrollToEnd(); }
        private void CatalogClicked(object sender, RoutedEventArgs e) { LogExpander.IsExpanded = false; SearchBox.Focus(); }
        private void OpenLogClicked(object sender, RoutedEventArgs e)
        { if (File.Exists(log.FilePath)) Process.Start(new ProcessStartInfo("notepad.exe", ProcessRunner.Quote(log.FilePath)) { UseShellExecute = false }); }
        private void HelpClicked(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(this,
                "1. Zaznacz programy lub użyj przycisku przy pojedynczym programie.\n2. Kliknij „Zainstaluj wybrane” albo „Odinstaluj wybrane”.\n3. Potwierdź listę i śledź stan oraz dziennik.\n\n" +
                "„Zainstaluj wszystkie” obejmuje cały katalog, także programy ukryte przez filtr. Instalacja aktualizuje istniejące programy. Kolejka wykonuje operacje po kolei. „Zatrzymaj po bieżącym” pozwala instalatorowi bezpiecznie dokończyć pracę.\n\n" +
                "Katalog: Synology Chat, Synology Drive, 7-Zip i Google Chrome. Po instalacji aplikacji Synology skonfiguruj połączenie ze swoim serwerem NAS.\n\n" +
                "Wymagania: Windows 10 (1809+) lub Windows 11, .NET Framework 4.8 lub nowszy i internet. Program działa z uprawnieniami administratora. Jeśli WinGet jest niedostępny, zostanie przygotowany automatycznie.\n\n" +
                "Plik EXE nie wymaga instalacji ani towarzyszących DLL. Dzienniki są zapisywane w:\n" + Path.Combine(SessionLog.DataFolder, "Logs"),
                "Jak działa Postinstall", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        private void MoreClicked(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender; var item = (ProgramItem)button.Tag;
            var menu = new ContextMenu();
            var website = new MenuItem { Header = "Strona programu" };
            website.Click += (s, args) => Process.Start(new ProcessStartInfo(item.Definition.Website) { UseShellExecute = true });
            menu.Items.Add(website);
            var details = new MenuItem { Header = "Status i licencja" };
            details.Click += (s, args) => MessageBox.Show(this, item.Definition.Name + "\n\n" + item.Detail + "\n\nLicencja: " + item.Definition.License, "Szczegóły programu", MessageBoxButton.OK, MessageBoxImage.Information);
            menu.Items.Add(details);
            button.ContextMenu = menu; menu.PlacementTarget = button; menu.IsOpen = true;
        }
        private void MinimizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void MaximizeClicked(object sender, RoutedEventArgs e)
        { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
        private void CloseClicked(object sender, RoutedEventArgs e) => Close();
        private void WindowClosing(object sender, CancelEventArgs e)
        {
            if (vm.QueueActive)
            {
                e.Cancel = true;
                vm.Stopping = true; vm.Activity = "Zaczekaj na zakończenie bieżącej operacji, potem zamknij program.";
                log.Write("Zamknięcie podczas instalacji: zatrzymano kolejkę po bieżącym programie.");
                return;
            }
            log.LineAdded -= OnLogLine; engine.Dispose();
        }
    }
}
