namespace GpuAgent;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "GpuAgent-SingleInstance-7F3A9B2C", out var owned);
        if (!owned) return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
    }
}
