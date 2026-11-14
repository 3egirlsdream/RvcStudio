using Avalonia;

namespace RvcStudio.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (FullInstallCleanup.Handle(args) || UpdateRunner.Handle(args)) return;
        using var gate = new Mutex(false, UpdateRunner.MutexName(AppContext.BaseDirectory));
        bool acquired;
        try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return;
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { gate.ReleaseMutex(); }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
