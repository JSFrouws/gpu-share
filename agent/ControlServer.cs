using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GpuAgent;

class ControlServer : IDisposable
{
    private readonly HttpListener _http = new();
    private static readonly HttpClient _lmHttp = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly string _token;
    private readonly TrayApp _tray;
    private readonly CancellationTokenSource _cts = new();

    public ControlServer(int port, string token, TrayApp tray)
    {
        _token = token;
        _tray = tray;

        // Try LAN-wide; falls back to localhost if no URL ACL is registered.
        // Run install.ps1 once (as admin) to register the ACL for LAN access.
        try
        {
            _http.Prefixes.Add($"http://+:{port}/");
            _http.Start();
        }
        catch (HttpListenerException)
        {
            _http.Prefixes.Clear();
            _http.Prefixes.Add($"http://localhost:{port}/");
            _http.Start();
        }

        Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext? ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            if (req.Headers["Authorization"] != $"Bearer {_token}")
            { Reply(res, 401, "Unauthorized"); return; }

            var path = req.Url?.AbsolutePath ?? "/";

            if (req.HttpMethod == "GET" && path == "/models")
            {
                // Proxy LM Studio's model list so life-os can populate its picker.
                try
                {
                    var json = _lmHttp.GetStringAsync("http://127.0.0.1:1234/v1/models").GetAwaiter().GetResult();
                    Reply(res, 200, json, "application/json");
                }
                catch (Exception)
                {
                    Reply(res, 503, "LM Studio not running");
                }
                return;
            }

            if (req.HttpMethod == "GET" && path == "/models/state")
            {
                // LM Studio's native (non-OpenAI) list: includes per-model
                // "state": "loaded"|"not-loaded" so life-os can show whether a
                // model is actually in VRAM vs. just present on disk.
                try
                {
                    var json = _lmHttp.GetStringAsync("http://127.0.0.1:1234/api/v0/models").GetAwaiter().GetResult();
                    Reply(res, 200, json, "application/json");
                }
                catch (Exception)
                {
                    Reply(res, 503, "LM Studio not running");
                }
                return;
            }

            if (req.HttpMethod == "GET" && path == "/status")
            {
                Reply(res, 200, JsonSerializer.Serialize(new
                {
                    gpuHandler = _tray.GpuHandlerOn,
                    tunnel = _tray.TunnelOn,
                    lmStudio = _tray.LmRunning,
                    dinoWorker = _tray.DinoRunning,
                    cloudflared = _tray.CloudflaredRunning,
                }), "application/json");
                return;
            }

            if (req.HttpMethod == "POST") switch (path)
            {
                case "/gpu/on":  _tray.Post(_tray.TurnGpuOn);    Reply(res, 200, "ok"); return;
                case "/gpu/off": _tray.Post(_tray.TurnGpuOff);   Reply(res, 200, "ok"); return;
                case "/tunnel/on":  _tray.Post(_tray.TurnTunnelOn);  Reply(res, 200, "ok"); return;
                case "/tunnel/off": _tray.Post(_tray.TurnTunnelOff); Reply(res, 200, "ok"); return;
                case "/power/shutdown":
                    Process.Start(new ProcessStartInfo("shutdown") { Arguments = "/s /t 5", UseShellExecute = true });
                    Reply(res, 200, "ok"); return;
                case "/power/hibernate":
                    Process.Start(new ProcessStartInfo("shutdown") { Arguments = "/h", UseShellExecute = true });
                    Reply(res, 200, "ok"); return;
                case "/power/sleep":
                    // shutdown.exe has no sleep (S3) flag — SetSuspendState via rundll32 is the
                    // standard command-line trigger. Args: hibernate=0, forceCritical=1, disableWakeEvent=0.
                    Process.Start(new ProcessStartInfo("rundll32.exe") { Arguments = "powrprof.dll,SetSuspendState 0,1,0", UseShellExecute = true });
                    Reply(res, 200, "ok"); return;
            }

            Reply(res, 404, "Not found");
        }
        catch (Exception ex) { Reply(res, 500, ex.Message); }
        finally { res.Close(); }
    }

    private static void Reply(HttpListenerResponse r, int code, string body, string ct = "text/plain")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        r.StatusCode = code;
        r.ContentType = ct;
        r.ContentLength64 = bytes.Length;
        r.OutputStream.Write(bytes);
    }

    public void Dispose() { _cts.Cancel(); try { _http.Stop(); } catch { } }
}
