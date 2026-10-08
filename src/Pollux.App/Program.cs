using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Pollux.App.Services;

namespace Pollux.App;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        StartupLog.Mark("main");
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Single instance: a second launch hands its arguments to the resident process and exits.
        var key = MainWindowKey();
        var instance = AppInstance.FindOrRegisterForKey(key);
        if (instance.IsCurrent && args.Contains("--quit"))
        {
            return 0; // nothing running that could be quit
        }

        if (!instance.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            instance.RedirectActivationToAsync(activation).AsTask().Wait();
            return 0;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App(args);
        });
        return 0;
    }

    /// <summary>Elevated and normal Pollux are separate instances (the Admin Instance runs side by side).</summary>
    private static string MainWindowKey() => Views.MainWindow.IsElevated ? "Pollux.Admin" : "Pollux";
}
