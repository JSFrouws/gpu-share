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
                // vramUsedMb makes "model loaded but living in system RAM" (the
                // post-resume eviction state) visible instead of silent: inference
                // mode on + a near-empty card means the weights aren't on the GPU.
                var (vramUsed, vramTotal) = VramMb();
                Reply(res, 200, JsonSerializer.Serialize(new
                {
                    gpuHandler = _tray.GpuHandlerOn,
                    tunnel = _tray.TunnelOn,
                    lmStudio = _tray.LmRunning,
                    dinoWorker = _tray.DinoRunning,
                    cloudflared = _tray.CloudflaredRunning,
                    vramUsedMb = vramUsed,
                    vramTotalMb = vramTotal,
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
                    // Application.SetSuspendState honors the hibernate flag directly (unlike the
                    // rundll32 SetSuspendState entry point, which ignores its arguments).
                    Reply(res, 200, "ok");
                    SuspendAfterUnload(PowerState.Hibernate);
                    return;
                case "/power/sleep":
                    // PowerState.Suspend gives true S3 sleep regardless of whether hibernation is
                    // enabled. The old rundll32 SetSuspendState route ignored its args and hibernated
                    // whenever hibernation was enabled on the machine.
                    Reply(res, 200, "ok");
                    SuspendAfterUnload(PowerState.Suspend);
                    return;
            }

            Reply(res, 404, "Not found");
        }
        catch (Exception ex) { Reply(res, 500, ex.Message); }
        finally { res.Close(); }
    }

    private static (int used, int total) _vram = (-1, -1);
    private static DateTime _vramAt = DateTime.MinValue;

    /// <summary>Card memory via nvidia-smi, cached briefly (status is polled).
    /// (-1, -1) when nvidia-smi isn't available.</summary>
    private static (int, int) VramMb()
    {
        if (DateTime.UtcNow - _vramAt < TimeSpan.FromSeconds(5)) return _vram;
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi")
            {
                Arguments = "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            var line = p?.StandardOutput.ReadLine() ?? "";
            p?.WaitForExit(3000);
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out var u) && int.TryParse(parts[1], out var t))
                _vram = (u, t);
        }
        catch { _vram = (-1, -1); }
        _vramAt = DateTime.UtcNow;
        return _vram;
    }

    /// <summary>Drop the model, then suspend. Answering the caller first matters:
    /// SetSuspendState does not return until the machine wakes up again, so a
    /// reply afterwards would leave life-os waiting on a dead socket until then.
    /// The unload keeps ~20 GB of weights from being evicted into system RAM
    /// (see TrayApp's sleep/wake notes); PowerModeChanged covers the same ground
    /// for sleeps we don't initiate, and a second unload is harmless.</summary>
    private void SuspendAfterUnload(PowerState state)
    {
        Task.Run(() =>
        {
            try { _tray.PrepareForSuspend(); } catch { /* never block the suspend */ }
            Thread.Sleep(1500);   // let VRAM actually drain before the GPU powers down
            Application.SetSuspendState(state, false, false);
        });
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
