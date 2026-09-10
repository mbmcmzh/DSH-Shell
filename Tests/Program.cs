using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using DSHShell;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

if (args.Contains("--real"))
{
    // 使用独立端口和临时 DSH_HOME，不接触用户会话或占用正常实例的端口。
    var reservation = new TcpListener(IPAddress.Loopback, 0);
    reservation.Start();
    var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
    reservation.Stop();
    Environment.SetEnvironmentVariable("DSH_SHELL_PORT", port.ToString());
    var home = Path.Combine(Path.GetTempPath(), "dsh-shell-smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(home);
    Environment.SetEnvironmentVariable("DSH_HOME", home);
    var server = new ServerManager(new AppSettings());
    try
    {
        server.Start(Path.Combine(home, "server.log"));
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!server.IsPortOpen())
        {
            if (server.HasExited) throw new Exception("Real dsh exited; inspect isolated smoke log in " + home);
            if (DateTime.UtcNow > deadline) throw new Exception("Real dsh startup timed out: " + home);
            await Task.Delay(250);
        }
        await server.ResolveWebUrlAsync();
        Check(new Uri(server.WebUrl).Query.Contains("token="), "real dsh launch token captured");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        Check((await client.GetAsync($"http://127.0.0.1:{port}/")).StatusCode == HttpStatusCode.Unauthorized,
            "real dsh reproduces original 401 without authentication");
        using var response = await client.GetAsync(server.WebUrl);
        Check(response.StatusCode == HttpStatusCode.OK, "real dsh token exchange redirects to usable frontend");
        Check(response.RequestMessage.RequestUri.Query == "", "real dsh removes token after exchange");
        Check((await client.GetAsync($"http://127.0.0.1:{port}/")).StatusCode == HttpStatusCode.OK,
            "real dsh cookie permits reload");
    }
    finally { server.Stop(); }
    Check(!server.IsPortOpen(), "owned dsh service stopped");
    return;
}

await using var endpoint = new TestEndpoint();
var address = new DshWebAddress(endpoint.Port);
string Root(string host = "127.0.0.1") => $"http://{host}:{endpoint.Port}/";
var tokenUrl = Root() + "?token=test-token";
address.Capture("unrelated " + tokenUrl);
Check(address.Url == Root(), "unrelated log URLs ignored");
address.Capture("\u001b[32mdsh web:\u001b[0m " + Root("localhost") + "?token=test-token (LAN: http://192.168.1.2/)");
Check(address.Url == tokenUrl, "ANSI announcement keeps token and normalizes localhost");
Check(address.TryNormalize(Root("[::1]") + "?token=a%2Bb&extra=1", out var normalized) &&
      normalized == Root() + "?token=a%2Bb&extra=1", "IPv6 and encoded query preserved");
foreach (var invalid in new[] { "https://127.0.0.1/", "http://evil.example/", Root() + "path?token=x",
    Root() + "#token=x", $"http://user@127.0.0.1:{endpoint.Port}/", "http://127.0.0.1:1/?token=x" })
    Check(!address.TrySet(invalid) && address.Url == tokenUrl, "invalid origin/path cannot replace launch URL");

var delayed = new DshWebAddress(endpoint.Port);
var pending = delayed.ResolveAsync(true, "unused.log");
await Task.Delay(600);
Check(!pending.IsCompleted, "open port with 401 waits for delayed announcement");
delayed.Capture("dsh web: " + tokenUrl);
await pending.WaitAsync(TimeSpan.FromSeconds(3));
Check(delayed.Url == tokenUrl, "delayed announcement unblocks startup");

endpoint.RequiresAuth = false;
var legacy = new DshWebAddress(endpoint.Port);
await legacy.ResolveAsync(true, "unused.log").WaitAsync(TimeSpan.FromSeconds(3));
Check(legacy.Url == Root(), "legacy server without authentication still opens");
endpoint.RequiresAuth = true;

var log = Path.GetTempFileName();
try
{
    await File.WriteAllTextAsync(log, "dsh web: " + tokenUrl + "\n");
    var reused = new DshWebAddress(endpoint.Port);
    await reused.ResolveAsync(false, log);
    Check(reused.Url == tokenUrl, "existing service recovers validated launch URL from shared log");
    await File.WriteAllTextAsync(log, "dsh web: " + Root() + "?token=stale\n");
    var stale = new DshWebAddress(endpoint.Port);
    await stale.ResolveAsync(false, log);
    Check(stale.Url == Root(), "stale launch token is rejected");
    var missing = new DshWebAddress(endpoint.Port);
    await missing.ResolveAsync(false, log + ".missing");
    Check(missing.Url == Root(), "external service without log can use browser cookie or manual entry");
}
finally { File.Delete(log); }

sealed class TestEndpoint : IAsyncDisposable
{
    private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly Task _serve;
    public volatile bool RequiresAuth = true;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public TestEndpoint() { _listener.Start(); _serve = ServeAsync(); }
    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                using var connection = await _listener.AcceptTcpClientAsync();
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var request = await reader.ReadLineAsync();
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                var status = !RequiresAuth ? "200 OK" : "401 Unauthorized";
                var headers = "";
                if (request?.Contains("/?token=test-token ") == true)
                {
                    status = "303 See Other";
                    headers = "Location: /\r\nSet-Cookie: dsh-auth-test=signed; Path=/; HttpOnly\r\n";
                }
                var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{headers}Content-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(bytes);
            }
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync() { _listener.Stop(); await _serve; }
}
