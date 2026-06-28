using System.Diagnostics;

namespace GpuAgent;

class Supervisor(string name, ProcessDef def)
{
    private Process? _proc;

    public bool IsRunning
    {
        get
        {
            if (_proc is null) return false;
            try { return !_proc.HasExited; }
            catch { return false; }
        }
    }

    public void Start()
    {
        if (IsRunning) return;
        var exe = Environment.ExpandEnvironmentVariables(def.Executable);
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            Log($"Executable not found: {exe}");
            return;
        }
        var si = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in def.Args) si.ArgumentList.Add(arg);
        if (!string.IsNullOrEmpty(def.WorkingDir))
            si.WorkingDirectory = Environment.ExpandEnvironmentVariables(def.WorkingDir);
        foreach (var (k, v) in def.Env) si.Environment[k] = v;
        try
        {
            _proc = Process.Start(si);
            Log($"Started {name} PID={_proc?.Id}");
        }
        catch (Exception ex)
        {
            Log($"Failed to start {name}: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_proc is null) return;
        try
        {
            if (!_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
                Log($"Stopped {name}");
            }
        }
        catch (Exception ex) { Log($"Kill error {name}: {ex.Message}"); }
        finally { _proc = null; }
    }

    private static void Log(string msg) =>
        File.AppendAllText(
            Path.Combine(AppContext.BaseDirectory, "agent.log"),
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
}
