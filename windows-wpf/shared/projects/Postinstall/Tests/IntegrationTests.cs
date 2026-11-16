using Postinstall.Models;
using Postinstall.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Xml;

internal static class IntegrationTests
{
    public sealed class TestResult { public string name { get; set; } public bool passed { get; set; } public string detail { get; set; } }
    private static readonly List<TestResult> results = new List<TestResult>();
    private static SessionLog log;
    private static string report;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--echo")
        { Console.WriteLine(new JavaScriptSerializer().Serialize(args.Skip(1).ToArray())); return 0; }
        report = args.Length > 1 ? args[1] : "test-results.json";
        log = new SessionLog(); log.LineAdded += Console.WriteLine;
        try
        {
            if (args.Length > 0 && args[0] == "--ui") UiTests();
            else if (args.Length > 0 && args[0] == "--ui-queue") UiQueueTests();
            else RunAsync(args.Length > 0 ? args[0] : "--read-only").GetAwaiter().GetResult();
        }
        catch (Exception e) { Console.WriteLine(e); results.Add(new TestResult { name = "unhandled", passed = false, detail = e.ToString() }); }
        File.WriteAllText(report, new JavaScriptSerializer().Serialize(new { timestamp = DateTimeOffset.Now.ToString("o"), results, log = log.FilePath }));
        return results.Any(x => !x.passed) ? 1 : 0;
    }

    private static void Check(string name, bool condition, string detail = "")
    {
        results.Add(new TestResult { name = name, passed = condition, detail = detail });
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name + " " + detail);
    }

    private static async Task RunAsync(string mode)
    {
        if (mode != "--read-only" && mode != "--sources" && mode != "--lifecycle")
            throw new ArgumentException("Unknown test mode: " + mode);
        var catalog = Catalog.Create();
        Check("Exactly the four requested applications in order", catalog.Select(a => a.Name).SequenceEqual(new[] { "Synology Chat", "Synology Drive", "7-Zip", "Google Chrome" }) && catalog.Select(a => a.Id).Distinct().Count() == 4);
        var chat = Catalog.Find("synology-chat");
        var drive = Catalog.Find("synology-drive");
        Check("Synology Chat registry names detected", new[] { "Synology Chat", "Synology Chat Client", "Synology Chat 1.2.5", "Synology Chat Client 1.2.5-0240" }.All(chat.Matches));
        Check("Synology Drive registry names detected", new[] { "Synology Drive", "Synology Drive Client", "Synology Drive Client 4.2.1", "Synology Drive Client 4.2.1-20062 (x64)" }.All(drive.Matches));
        Check("Synology clients exclude other products", new[] { "Synology Chat Server", "Synology Drive Server", "Synology Drive ShareSync", "Synology Active Backup for Business Agent" }.All(n => !chat.Matches(n) && !drive.Matches(n)));
        Check("Existing products detected", Catalog.Find("7zip").Matches("7-Zip 25.01 (x64)") && Catalog.Find("chrome").Matches("Google Chrome") && !Catalog.Find("chrome").Matches("Google Chrome Beta"));
        var fixture = new InstalledSnapshot { Programs = new[] {
            new InstalledProgram { Name = "Synology Chat", Version = "1.2.5" },
            new InstalledProgram { Name = "Synology Drive Client", Version = "4.2.1" },
            new InstalledProgram { Name = "Synology Drive Server", Version = "9.0" }
        } };
        Check("Synology installations and versions detected independently", fixture.Find(chat).Count == 1 && fixture.Version(chat) == "1.2.5" && fixture.Find(drive).Count == 1 && fixture.Version(drive) == "4.2.1");
        Check("Embedded .NET Framework target", Assembly.GetAssembly(typeof(Catalog)).GetCustomAttributesData().Any(a => a.AttributeType.Name == "TargetFrameworkAttribute" && a.ConstructorArguments[0].Value.ToString() == ".NETFramework,Version=v4.8"));
        var rejected = false;
        try { Downloads.RequireHttps("http://example.com/setup.exe"); } catch (InvalidOperationException) { rejected = true; }
        Check("HTTP rejected", rejected);
        rejected = false;
        try { Downloads.RequireHost("https://corel.com.evil.example/setup.exe", "corel.com"); } catch (InvalidOperationException) { rejected = true; }
        Check("Lookalike domains rejected", rejected);
        string exe, command;
        InstallationEngine.ParseCommand(@"C:\Program Files\My App\uninstall.exe /uninstall", out exe, out command);
        Check("Unquoted uninstall path preserved", exe == @"C:\Program Files\My App\uninstall.exe" && command == "/uninstall");
        var runner = new ProcessRunner(log);
        var inputs = new[] { "space in path", "quoted \"content\"", "trailing\\", "quote\"\\", "&echo should not execute", "apostrophe's" };
        var echo = await runner.RunAsync(Assembly.GetExecutingAssembly().Location, "--echo " + string.Join(" ", inputs.Select(ProcessRunner.Quote)), logOutput: false);
        var decoded = new JavaScriptSerializer().Deserialize<string[]>(echo.Output.Trim());
        Check("Windows quoting roundtrip", echo.ExitCode == 0 && decoded.SequenceEqual(inputs));
        var bad = await runner.PowerShellAsync("throw 'Expected failure'", logOutput: false);
        Check("PowerShell errors return failure", bad.ExitCode != 0 && bad.Output.Contains("Expected failure"));

        using (var engine = new InstallationEngine(log))
        {
            var initial = await engine.Inventory.ScanAsync();
            Check("Inventory read", initial.Programs.Count > 0, initial.Programs.Count.ToString());
            Check("WinGet detected", await engine.Winget.ProbeAsync(), engine.Winget.Version);
            if (mode == "--read-only") return;
            if (mode == "--sources")
            {
                foreach (var app in catalog.Where(a => a.WingetId != null))
                {
                    var show = await runner.RunAsync(engine.Winget.Executable,
                        "show --id " + ProcessRunner.Quote(app.WingetId) + " --exact --source winget --accept-source-agreements --disable-interactivity", logOutput: false);
                    Check("Live catalogue " + app.Id, show.ExitCode == 0, show.Output.Trim());
                }
                return;
            }
            await engine.Winget.EnsureAsync(CancellationToken.None, Console.WriteLine);
            foreach (var app in catalog)
            {
                if (initial.IsInstalled(app))
                {
                    Console.WriteLine("SKIP lifecycle of pre-existing installation: " + app.Name);
                    continue;
                }
                try
                {
                    var installed = await engine.InstallAsync(app, CancellationToken.None, Console.WriteLine);
                    Check("Real install " + app.Id, installed.Snapshot.IsInstalled(app), installed.Message);
                    var removed = await engine.UninstallAsync(app, Console.WriteLine);
                    Check("Real uninstall " + app.Id, !removed.Snapshot.IsInstalled(app), removed.Message);
                }
                catch (Exception e) { Check("Operation " + app.Id, false, e.ToString()); }
            }
        }
    }

    private static AutomationElement Find(AutomationElement parent, string name)
    { return parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name)); }

    private static void UiTests()
    {
        var proc = Process.GetProcessesByName("Postinstall").Single();
        var window = AutomationElement.FromHandle(proc.MainWindowHandle);
        Check("WPF window responding", proc.Responding && window != null);
        WaitUi(() => Find(window, "Gotowe. Wybierz programy dla siebie.") != null, 30);
        Check("Four requested programs visible", Catalog.Create().All(a => Find(window, a.Name) != null));
        Check("Catalogue count is current", Find(window, "Wyświetlono 4 z 4 programów") != null);
        var search = Find(window, "Szukaj programu");
        Check("Search accessible", search != null);
        var value = (ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern);
        value.SetValue("Synology"); Thread.Sleep(600);
        var checkbox = Find(window, "Synology Chat");
        Check("Search filters the catalogue", checkbox != null && Find(window, "Synology Drive") != null && Find(window, "Google Chrome") == null);
        var toggle = (TogglePattern)checkbox.GetCurrentPattern(TogglePattern.Pattern);
        toggle.Toggle(); Thread.Sleep(400);
        var action = FindButton(window, "Zainstaluj wybrane");
        Check("Selecting enables install", action != null && action.Current.IsEnabled);
        value.SetValue("program-that-does-not-exist"); Thread.Sleep(400);
        Check("Empty search state", Find(window, "Nie znaleziono programów") != null);
        value.SetValue(""); Thread.Sleep(500);
        var clear = Find(window, "Odznacz"); ((InvokePattern)clear.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); Thread.Sleep(300);
        Check("Clear selection disables install", !action.Current.IsEnabled);
        var logs = Find(window, "Dziennik sesji"); ((InvokePattern)logs.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); Thread.Sleep(300);
        Check("Session log expands", Find(window, "Otwórz pełny dziennik") != null);
        var catalog = Find(window, "Katalog programów"); ((InvokePattern)catalog.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); Thread.Sleep(200);
        var maximize = Find(window, "Maksymalizuj"); ((InvokePattern)maximize.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); Thread.Sleep(400);
        Check("Window maximizes", ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Current.WindowVisualState == WindowVisualState.Maximized);
        ((InvokePattern)maximize.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); Thread.Sleep(300);
        Check("Window restores", ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Current.WindowVisualState == WindowVisualState.Normal);
    }

    private static void Invoke(AutomationElement element)
    { ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); }

    private static AutomationElement FindButton(AutomationElement parent, string name)
    { return parent.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.NameProperty, name), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))); }

    private static void WaitUi(Func<bool> condition, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(250);
        }
        throw new TimeoutException("Timed out waiting for the WPF operation.");
    }

    private static void Confirm(string title, bool accept)
    {
        AutomationElement dialog = null;
        WaitUi(() => (dialog = Find(AutomationElement.RootElement, title)) != null, 15);
        var button = FindButton(dialog, accept ? "Yes" : "No") ?? FindButton(dialog, accept ? "Tak" : "Nie");
        Invoke(button);
    }

    private static void UiQueueTests()
    {
        var proc = Process.GetProcessesByName("Postinstall").Single();
        var window = AutomationElement.FromHandle(proc.MainWindowHandle);
        WaitUi(() => Find(window, "Gotowe. Wybierz programy dla siebie.") != null, 30);
        var search = (ValuePattern)Find(window, "Szukaj programu").GetCurrentPattern(ValuePattern.Pattern);
        search.SetValue("7-Zip");
        Thread.Sleep(400);
        Invoke(FindButton(window, "Zainstaluj wszystkie"));
        AutomationElement dialog = null;
        WaitUi(() => (dialog = Find(AutomationElement.RootElement, "Potwierdź instalację")) != null, 15);
        var text = string.Join("\n", dialog.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Select(e => e.Current.Name));
        Check("Install all includes all four programs hidden by search", Catalog.Create().All(a => text.Contains(a.Name)));
        Check("Install all excludes removed programs", !text.Contains("CorelDRAW") && !text.Contains("PuTTY"));
        Confirm("Potwierdź instalację", false);
        search.SetValue("");
        Thread.Sleep(300);
        Check("Cancelling confirmation leaves UI ready", FindButton(window, "Zainstaluj wszystkie").Current.IsEnabled);
    }
}
