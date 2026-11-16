using System;
using System.IO;
using System.Text;

namespace Postinstall.Services
{
    public sealed class SessionLog
    {
        private readonly object gate = new object();
        public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Postinstall");
        public string FilePath { get; }
        public event Action<string> LineAdded;
        public SessionLog()
        {
            var folder = Path.Combine(DataFolder, "Logs");
            Directory.CreateDirectory(folder);
            FilePath = Path.Combine(folder, "sesja-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".log");
        }
        public void Write(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + text.Trim();
            lock (gate)
            {
                try { File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(false)); }
                catch (IOException) { }
            }
            LineAdded?.Invoke(line);
        }
    }
}
