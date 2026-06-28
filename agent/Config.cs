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
    public ProcessDef LmStudio { get; set; } = new()
    {
        Executable = @"%LOCALAPPDATA%\Programs\LM-Studio\LM Studio.exe"
    };
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
