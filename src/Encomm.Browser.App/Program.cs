using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Threading;

namespace Encomm.Browser.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Ensure the WindowsAppSDK runtime is loaded for this process when running
        // as an unpackaged desktop app. SelfContained mode ships the runtime in
        // the output directory, but the bootstrapper still needs to initialize it.
        try
        {
            Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.TryInitialize(
                0x00010007, "8wekyb3d8bbwe", out int _initResult);
        }
        catch
        {
            // bootstrap may not be available; we will fail later in XAML init.
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start((ApplicationInitializationCallbackParams p) =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App();
        });
        return 0;
    }
}