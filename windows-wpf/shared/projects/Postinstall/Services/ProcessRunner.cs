using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postinstall.Services
{
    public sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string HexCode => "0x" + unchecked((uint)ExitCode).ToString("X8");
    }

    public sealed class ProcessRunner
    {
        private readonly SessionLog log;
        public ProcessRunner(SessionLog log) { this.log = log; }

        // Installers are allowed to finish. Stopping a batch never kills an MSI process.
        public async Task<ProcessResult> RunAsync(string path, string arguments, bool interactive = false,
            int timeoutSeconds = 3600, bool logOutput = true, string workingDirectory = null)
        {
            using (var process = new Process())
            {
                var output = new StringBuilder();
                var gate = new object();
                process.StartInfo = new ProcessStartInfo(path, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = !interactive,
                    RedirectStandardOutput = !interactive,
                    RedirectStandardError = !interactive,
                    RedirectStandardInput = !interactive,
                    StandardOutputEncoding = interactive ? null : Encoding.UTF8,
                    StandardErrorEncoding = interactive ? null : Encoding.UTF8,
                    WorkingDirectory = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                };
                if (!interactive)
                {
                    DataReceivedEventHandler received = (s, e) =>
                    {
                        if (string.IsNullOrWhiteSpace(e.Data)) return;
                        lock (gate) { if (output.Length < 2000000) output.AppendLine(e.Data); }
                        if (logOutput && e.Data.Trim().Length > 2 && !IsSpinner(e.Data)) log.Write(e.Data);
                    };
                    process.OutputDataReceived += received;
                    process.ErrorDataReceived += received;
                }
                process.Start();
                if (!interactive)
                {
                    process.StandardInput.Close();
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                }
                var wait = Task.Run(() => process.WaitForExit());
                if (await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))) != wait)
                {
                    // Keep the process and its output handlers alive until it exits.
                    await wait;
                    log.Write("Proces przekroczył typowy czas wykonania; zaczekano na jego bezpieczne zakończenie.");
                }
                await wait;
                return new ProcessResult { ExitCode = process.ExitCode, Output = output.ToString() };
            }
        }

        private static bool IsSpinner(string line) => line.Trim().All(c => "-/\\| ".Contains(c));

        // Windows command line quoting; input is never interpreted by cmd.exe.
        public static string Quote(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var result = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in value)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') result.Append('\\', backslashes * 2 + 1).Append(c);
                else result.Append('\\', backslashes).Append(c);
                backslashes = 0;
            }
            return result.Append('\\', backslashes * 2).Append('"').ToString();
        }

        public Task<ProcessResult> PowerShellAsync(string script, int timeoutSeconds = 600, bool logOutput = true)
        {
            var header = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " +
                "[Console]::OutputEncoding=[Text.Encoding]::UTF8; try { ";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(header + script +
                " } catch { [Console]::Error.WriteLine(($_ | Out-String)); exit 1 }"));
            var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            return RunAsync(path, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                timeoutSeconds: timeoutSeconds, logOutput: logOutput);
        }

        public static string QuotePowerShell(string value) => "'" + value.Replace("'", "''") + "'";
    }
}
