using NasReader.Server;

namespace NasReader;

static class Program
{
    [STAThread]
    static void Main()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();

        var app = AppHost.Start();
        try
        {
            Application.Run(new MainForm(app.Url));
        }
        finally
        {
            // Send the last pages read, then install a downloaded update quietly.
            try { Sync.RunAsync().Wait(5000); } catch { }
            if (Settings.Current.AutoUpdate) Updater.Install(relaunch: false);
            app.Stop();
        }
    }
}
