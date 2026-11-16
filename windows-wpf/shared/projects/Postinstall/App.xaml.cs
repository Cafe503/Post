using System;
using System.Threading;
using System.Windows;

namespace Postinstall
{
    public partial class App : Application
    {
        private Mutex instance;
        private void OnStartup(object sender, StartupEventArgs e)
        {
            bool created;
            instance = new Mutex(true, @"Local\Postinstall-WPF-Net48", out created);
            if (!created)
            {
                MessageBox.Show("Postinstall jest już uruchomiony. Przejdź do otwartego okna programu.", "Postinstall", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(); return;
            }
            DispatcherUnhandledException += (s, exception) =>
            {
                MessageBox.Show("Wystąpił błąd: " + exception.Exception.Message + "\nSzczegóły operacji znajdziesz w dzienniku.", "Postinstall", MessageBoxButton.OK, MessageBoxImage.Error);
                exception.Handled = true;
            };
            var window = new MainWindow(); MainWindow = window; window.Show();
        }
        protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
    }
}
