using WaveSync.UI;

namespace WaveSync;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\WaveSync.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("WaveSync is already running. Look for its icon in the system tray.", "WaveSync",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        TrayApp app;
        try
        {
            app = new TrayApp();
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            MessageBox.Show($"WaveSync couldn't start:\n\n{ex.Message}", "WaveSync",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Log.Info("WaveSync started");
        Application.Run(app);
        Log.Info("WaveSync exited");
    }
}
