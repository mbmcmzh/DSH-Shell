using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DSHShell
{
    /// <summary>接收 dsh 宣告的入口地址，保留新版启动 token，统一使用壳的 loopback origin。</summary>
    internal sealed class DshWebAddress
    {
        private static readonly Regex Ansi = new Regex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
        private static readonly Regex Announcement = new Regex(@"(?:^|\s)dsh web:\s*(http://[^\s<>""']+)", RegexOptions.Compiled);
        private readonly int _port;
        private readonly TaskCompletionSource<string> _announced = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile string _url;

        public DshWebAddress(int port) { _port = port; }
        public string BaseUrl => $"http://127.0.0.1:{_port}/";
        public string Url => _url ?? BaseUrl;

        public bool TryNormalize(string value, out string url)
        {
            url = null;
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttp || uri.Port != _port ||
                uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Fragment.Length != 0 ||
                !(uri.Host == "127.0.0.1" || uri.Host == "localhost" || uri.Host == "[::1]")) return false;

            // WSL 的 localhost/IPv6 宣告也通过 Windows 的固定 IPv4 转发访问。
            url = new UriBuilder(uri) { Host = "127.0.0.1" }.Uri.AbsoluteUri;
            return true;
        }

        private bool TryParseAnnouncement(string line, out string url)
        {
            var match = Announcement.Match(Ansi.Replace(line ?? string.Empty, string.Empty));
            url = null;
            return match.Success && TryNormalize(match.Groups[1].Value, out url);
        }

        public void Capture(string line)
        {
            if (!TryParseAnnouncement(line, out var url)) return;
            _url = url;
            _announced.TrySetResult(url);
        }

        public bool TrySet(string value)
        {
            if (!TryNormalize(value, out var url)) return false;
            _url = url;
            return true;
        }

        public async Task ResolveAsync(bool startedByUs, string logPath)
        {
            // 探测不带浏览器 cookie，不跟随重定向，也不把本地 token 发给系统代理。
            using var client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false, UseProxy = false
            }) { Timeout = TimeSpan.FromSeconds(2) };

            if (!startedByUs)
            {
                // 上一个壳可能仍留着服务。日志中的 token 必须经当前服务验证，不能盲用旧 token。
                var candidate = ReadLastAnnouncement(logPath);
                if (candidate != null && await CanOpenAsync(client, candidate)) _url = candidate;
                return; // 没有可用入口时先让 WebView 尝试已有 cookie；401 时提供粘贴入口。
            }

            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (_announced.Task.IsCompleted) return;
                // 老版不需要认证，仍可直接访问；新版即使端口先打开，也必须等到 token 输出。
                if (await CanOpenAsync(client, BaseUrl)) return;
                if (_announced.Task.IsCompleted) return;
                await Task.WhenAny(_announced.Task, Task.Delay(250));
            }
            throw new Exception("dsh 端口已开放，但尚未获得可用的网页入口。请检查 dsh 启动输出中是否有“dsh web: http://...”地址。\r\n日志：" + logPath);
        }

        private static async Task<bool> CanOpenAsync(HttpClient client, string url)
        {
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                return response.StatusCode == HttpStatusCode.OK ||
                       (response.StatusCode == HttpStatusCode.SeeOther &&
                        response.Headers.Location?.OriginalString == "/" &&
                        response.Headers.Contains("Set-Cookie"));
            }
            catch (HttpRequestException) { return false; }
            catch (TaskCanceledException) { return false; }
        }

        private string ReadLastAnnouncement(string logPath)
        {
            try
            {
                using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                // 服务日志可能很大，只读尾部，且不将 token 另存到设置或缓存文件。
                if (stream.Length > 65536) stream.Seek(-65536, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                string candidate = null;
                string line;
                while ((line = reader.ReadLine()) != null)
                    if (TryParseAnnouncement(line, out var url)) candidate = url;
                return candidate;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
