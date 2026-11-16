using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Postinstall.Services
{
    public sealed class Downloads : IDisposable
    {
        private readonly HttpClient client;
        private readonly SessionLog log;
        public Downloads(SessionLog log)
        {
            this.log = log;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
            { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Postinstall/1.0");
        }

        public async Task<string> TextAsync(string url, CancellationToken token)
        {
            RequireHttps(url);
            using (var response = await client.GetAsync(url, token))
            { response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(); }
        }

        public async Task<string> PostJsonAsync(string url, object body, CancellationToken token)
        {
            RequireHttps(url);
            using (var content = new StringContent(new JavaScriptSerializer().Serialize(body), Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync(url, content, token))
            { response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(); }
        }

        public async Task FileAsync(string url, string destination, CancellationToken token, Action<string> progress = null, string sha256 = null)
        {
            RequireHttps(url);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            log.Write("Pobieranie: " + url);
            var partial = destination + ".part";
            try
            {
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    response.EnsureSuccessStatusCode();
                    RequireHttps(response.RequestMessage.RequestUri.AbsoluteUri);
                    using (var input = await response.Content.ReadAsStreamAsync())
                    using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        long total = 0;
                        var size = response.Content.Headers.ContentLength;
                        var lastUpdate = DateTime.MinValue;
                        int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, read, token); total += read;
                            if (DateTime.UtcNow - lastUpdate > TimeSpan.FromMilliseconds(600))
                            {
                                progress?.Invoke(size.HasValue && size.Value > 0
                                    ? "Pobieranie " + (total * 100 / size.Value) + "% · " + (total / 1048576.0).ToString("F1") + " MB"
                                    : "Pobieranie · " + (total / 1048576.0).ToString("F1") + " MB");
                                lastUpdate = DateTime.UtcNow;
                            }
                        }
                        if (size.HasValue && total != size.Value) throw new IOException("Pobrany plik jest niekompletny.");
                        if (total == 0) throw new IOException("Serwer zwrócił pusty plik.");
                    }
                }
                if (!string.IsNullOrWhiteSpace(sha256))
                {
                    using (var hash = SHA256.Create())
                    using (var input = File.OpenRead(partial))
                    {
                        var actual = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "");
                        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Suma SHA-256 instalatora jest niezgodna ze źródłem. Plik nie zostanie uruchomiony.");
                    }
                    log.Write("SHA-256 zgodne ze źródłem.");
                }
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(partial, destination);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }

        public static Uri RequireHttps(string url)
        {
            var uri = new Uri(WebUtility.HtmlDecode(url.Trim()));
            if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidOperationException("Źródło pobierania musi używać HTTPS.");
            return uri;
        }

        public static string RequireHost(string url, params string[] hosts)
        {
            var uri = RequireHttps(url);
            foreach (var host in hosts)
                if (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)) return uri.AbsoluteUri;
            throw new InvalidOperationException("Nieznana domena instalatora: " + uri.Host);
        }

        public void Dispose() => client.Dispose();
    }
}
