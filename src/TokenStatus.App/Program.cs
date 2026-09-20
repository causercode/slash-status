using System.Threading;

namespace TokenStatus.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        using var mutex = new Mutex(true, "Local\\TokenStatus", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        Application.ThreadException += (_, args) =>
        {
            System.Diagnostics.Debug.WriteLine(args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }
        };

        Application.Run(new TokenStatusApplicationContext(mutex));
    }
}
