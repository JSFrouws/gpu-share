using System.Text.Json;

namespace GpuAgent;

class ProcessDef
{
    public string Executable { get; set; } = "";
    public string[] Args { get; set; } = [];
    public string? WorkingDir { get; set; }
    public Dictionary<string, string> Env { get; set; } = new();
}

class Config
{
    public int ControlPort { get; set; } = 9000;
    public string BearerToken { get; set; } = "change-me";
    public string LmsPath { get; set; } = @"%USERPROFILE%\.lmstudio\bin\lms.exe";
    // Model key as shown by 'lms ls'. Leave empty to skip auto-load (model must be pre-loaded in LM Studio GUI).
    public string LmsModel { get; set; } = "";
    // Context length in tokens passed to 'lms load -c'. 0 = let LM Studio use the model default.
    // Larger context = more VRAM (KV cache). Use 'lms load <model> --gpu max -c <n> --estimate-only'
    // to size it against your card.
    public int LmsContextLength { get; set; } = 0;
    // Seconds to wait after resuming from sleep/hibernate before reloading the
    // model. The NVIDIA driver re-enumerates the GPU asynchronously on wake; a
    // load fired too early can land while VRAM is still unavailable.
    public int ResumeReloadDelaySeconds { get; set; } = 20;
    public ProcessDef DinoWorker { get; set; } = new()
    {
        Executable = @".venv\Scripts\uvicorn.exe",
        Args = ["app:app", "--host", "0.0.0.0", "--port", "8000"],
        Env = new() { ["SHARED_TOKEN"] = "change-me" }
    };
    public ProcessDef Cloudflared { get; set; } = new()
    {
        Executable = "cloudflared.exe",
        Args = ["tunnel", "run"]
    };

    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static Config Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            var defaults = new Config();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, _opts));
            return defaults;
        }
        try
        {
            return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), _opts) ?? new();
        }
        catch
        {
            return new();
        }
    }
}
