using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GpuAgent;

class TrayApp : ApplicationContext
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly Config _cfg;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _modeItem;
    private readonly ToolStripMenuItem _tunnelItem;
    private readonly Supervisor _dino;
    private readonly Supervisor _cloudflared;
    private readonly ControlServer _server;
    private readonly System.Windows.Forms.Timer _timer;

    private readonly Queue<Action> _queue = new();
    private readonly object _qlock = new();

    private bool _gpuOn;
    private bool _tunnelOn;

    public bool GpuHandlerOn => _gpuOn;
    public bool TunnelOn => _tunnelOn;
    public bool LmRunning => IsPortOpen(1234);
    public bool DinoRunning => _dino.IsRunning;
    public bool CloudflaredRunning => _cloudflared.IsRunning;

    public TrayApp()
    {
        _cfg = Config.Load();
        _dino = new Supervisor("dino-worker", _cfg.DinoWorker);
        _cloudflared = new Supervisor("cloudflared", _cfg.Cloudflared);

        // ── context menu ─────────────────────────────────────────────────────
        _statusItem = new ToolStripMenuItem("● Gaming mode") { Enabled = false };

        _modeItem = new ToolStripMenuItem("Start Inference (LAN)")
        {
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold)
        };
        _modeItem.Click += (_, _) => { if (_gpuOn) TurnGpuOff(); else TurnGpuOn(); };

        _tunnelItem = new ToolStripMenuItem("Enable Public Tunnel") { Enabled = false };
        _tunnelItem.Click += (_, _) => { if (_tunnelOn) TurnTunnelOff(); else TurnTunnelOn(); };

        var shutdownItem = new ToolStripMenuItem("Shutdown PC");
        shutdownItem.Click += (_, _) =>
        {
            if (MessageBox.Show("Shutdown this PC?", "GPU Agent",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            { StopAll(); Process.Start(new ProcessStartInfo("shutdown") { Arguments = "/s /t 0", UseShellExecute = true }); }
        };

        var hibernateItem = new ToolStripMenuItem("Hibernate PC");
        hibernateItem.Click += (_, _) =>
        {
            if (MessageBox.Show("Hibernate this PC?", "GPU Agent",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            { StopAll(); Process.Start(new ProcessStartInfo("shutdown") { Arguments = "/h", UseShellExecute = true }); }
        };

        var exitItem = new ToolStripMenuItem("Exit Agent");
        exitItem.Click += (_, _) => { StopAll(); Application.Exit(); };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_modeItem);
        menu.Items.Add(_tunnelItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(shutdownItem);
        menu.Items.Add(hibernateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        // ── tray icon ────────────────────────────────────────────────────────
        _tray = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
        };
        UpdateIcon();

        // ── UI-thread timer: drain the command queue + refresh icon ──────────
        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => { DrainQueue(); UpdateIcon(); };
        _timer.Start();

        // ── control server ───────────────────────────────────────────────────
        _server = new ControlServer(_cfg.ControlPort, _cfg.BearerToken, this);
    }

    // ── thread-safe command queue (called from ControlServer thread) ─────────

    public void Post(Action action) { lock (_qlock) _queue.Enqueue(action); }

    private void DrainQueue()
    {
        while (true)
        {
            Action? action;
            lock (_qlock) { if (_queue.Count == 0) break; action = _queue.Dequeue(); }
            action();
        }
    }

    // ── mode actions (always called on UI thread) ────────────────────────────

    public void TurnGpuOn()
    {
        _gpuOn = true;
        RunLms("start --bind 0.0.0.0 --cors");
        if (!string.IsNullOrWhiteSpace(_cfg.LmsModel))
            Task.Run(() => RunLmsLoad(_cfg.LmsModel));
        _dino.Start();
        UpdateIcon();
        _tray.ShowBalloonTip(3000, "GPU Share", "Inference started — loading model into VRAM…", ToolTipIcon.Info);
    }

    public void TurnGpuOff()
    {
        TurnTunnelOff();
        _gpuOn = false;
        if (!string.IsNullOrWhiteSpace(_cfg.LmsModel))
            RunLmsUnloadAll();
        RunLms("stop");
        _dino.Stop();
        UpdateIcon();
        _tray.ShowBalloonTip(3000, "GPU Share", "Gaming mode — VRAM fully free", ToolTipIcon.Info);
    }

    public void TurnTunnelOn()
    {
        if (!_gpuOn)
        {
            _tray.ShowBalloonTip(2000, "GPU Share", "Start inference first, then enable the tunnel.", ToolTipIcon.Warning);
            return;
        }
        _tunnelOn = true;
        _cloudflared.Start();
        UpdateIcon();
    }

    public void TurnTunnelOff()
    {
        _tunnelOn = false;
        _cloudflared.Stop();
        UpdateIcon();
    }

    private void RunLms(string command)
    {
        var lmsPath = Environment.ExpandEnvironmentVariables(_cfg.LmsPath);
        var psi = new ProcessStartInfo(lmsPath)
        {
            Arguments = $"server {command}",
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        try { Process.Start(psi); }
        catch (Exception ex) { Supervisor.Log($"lms server {command} failed: {ex.Message}"); }
    }

    private void RunLmsLoad(string modelKey)
    {
        var lmsPath = Environment.ExpandEnvironmentVariables(_cfg.LmsPath);
        var psi = new ProcessStartInfo(lmsPath)
        {
            Arguments = $"load \"{modelKey}\" --gpu max -y",
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        try { Process.Start(psi)?.WaitForExit(); }
        catch (Exception ex) { Supervisor.Log($"lms load failed: {ex.Message}"); }
    }

    private void RunLmsUnloadAll()
    {
        var lmsPath = Environment.ExpandEnvironmentVariables(_cfg.LmsPath);
        var psi = new ProcessStartInfo(lmsPath)
        {
            Arguments = "unload --all",
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        try { Process.Start(psi)?.WaitForExit(); }
        catch (Exception ex) { Supervisor.Log($"lms unload failed: {ex.Message}"); }
    }

    private static bool IsPortOpen(int port)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            return tcp.ConnectAsync("127.0.0.1", port).Wait(400);
        }
        catch { return false; }
    }

    private void StopAll()
    {
        RunLms("stop");
        _dino.Stop();
        _cloudflared.Stop();
    }

    // ── icon + tooltip update ────────────────────────────────────────────────

    private void UpdateIcon()
    {
        Color color;
        string label;
        string tip;
        string status;

        if (_tunnelOn)
        {
            color = Color.DodgerBlue; label = "PUB";
            tip = "GPU Share — Public Inference"; status = "● Public Inference";
        }
        else if (_gpuOn)
        {
            color = Color.LimeGreen; label = "INF";
            tip = "GPU Share — Inference (LAN)"; status = "● Inference (LAN)";
        }
        else
        {
            color = Color.DimGray; label = "GME";
            tip = "GPU Share — Gaming"; status = "● Gaming mode";
        }

        _statusItem.Text = status;
        _modeItem.Text = _gpuOn ? "Stop Inference  (gaming mode)" : "Start Inference  (LAN)";
        _tunnelItem.Text = _tunnelOn ? "Disable Public Tunnel" : "Enable Public Tunnel";
        _tunnelItem.Enabled = _gpuOn || _tunnelOn;

        var oldIcon = _tray.Icon;
        _tray.Icon = MakeIcon(color, label);
        _tray.Text = tip;
        oldIcon?.Dispose();
    }

    private static Icon MakeIcon(Color bg, string label)
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var fill = new SolidBrush(bg);
            g.FillEllipse(fill, 1, 1, 30, 30);

            using var border = new Pen(Color.FromArgb(100, 0, 0, 0), 1.5f);
            g.DrawEllipse(border, 1, 1, 30, 30);

            using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold, GraphicsUnit.Point);
            using var text = new SolidBrush(Color.White);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(label, font, text, new RectangleF(0, 0, 32, 32), sf);
        }

        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _server.Dispose();
            StopAll();
            _tray.Visible = false;
            _tray.Icon?.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
