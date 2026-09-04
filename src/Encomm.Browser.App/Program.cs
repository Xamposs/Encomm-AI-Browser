using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Encomm.Browser.App.Services;
using Encomm.Browser.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Encomm.Browser.App;

/// <summary>
/// Custom entry point.
///
/// We do our own startup so we can write a startup log before XAML touches
/// anything and surface failures as a real exit code. The WinAppSDK 2.x
/// auto-initializer handles UndockedRegFreeWinRT internally; we only
/// force-load Microsoft.WindowsAppRuntime.dll to ensure native symbols
/// are resolved before XAML activation.
/// </summary>
public static class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint DirectoryFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddDllDirectory(string NewDirectory);

    [DllImport("Microsoft.WindowsAppRuntime.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();

    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    private const uint LOAD_LIBRARY_SEARCH_APPLICATION_DIR = 0x00000200;
    private const uint LOAD_LIBRARY_SEARCH_USER_DIRS = 0x00000400;
    private const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x00000100;

    [STAThread]
    public static int Main(string[] args)
    {
        var paths = BrowserPaths.Default();
        Directory.CreateDirectory(paths.LogsDirectory);
        var logPath = Path.Combine(paths.LogsDirectory, "encomm.log");

        try
        {
            Log(logPath, "=== Encomm AI Browser startup ===");
            Log(logPath, $"Process: {Environment.ProcessId}, args=[{string.Join(",", args)}]");

            // 1) Set the DLL search order so the runtime DLLs in the bin
            //    directory are found before anything else.
            SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS |
                                     LOAD_LIBRARY_SEARCH_APPLICATION_DIR |
                                     LOAD_LIBRARY_SEARCH_USER_DIRS |
                                     LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
            AddDllDirectory(AppContext.BaseDirectory);
            Log(logPath, $"DLL search path: {AppContext.BaseDirectory}");

            // 2) Force-load the Windows App Runtime native DLL.
            try
            {
                int hr = WindowsAppRuntime_EnsureIsLoaded();
                Log(logPath, $"WindowsAppRuntime_EnsureIsLoaded hr=0x{hr:X8}");
            }
            catch (DllNotFoundException dnf)
            {
                Log(logPath, $"WindowsAppRuntime.dll missing: {dnf.Message}");
            }
            catch (Exception ex)
            {
                Log(logPath, $"WindowsAppRuntime load EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }

            // 3) Probe WebView2 runtime.
            try
            {
                var version = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
                Log(logPath, $"WebView2 runtime: {version}");
            }
            catch (Exception ex)
            {
                Log(logPath, $"WebView2 runtime probe FAILED: {ex.Message}");
            }

            // 4) Start XAML application on the UI thread.
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Log(logPath, "ComWrappers initialized. Starting XAML Application.");
            Application.Start((ApplicationInitializationCallbackParams p) =>
            {
                try
                {
                    var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(ctx);
                    Log(logPath, "XAML Application callback running.");
                    var app = new App();
                    Log(logPath, "App instance created successfully.");
                    var window = new MainWindow();
                    Log(logPath, "MainWindow constructed; calling Activate.");
                    window.Activate();
                    Log(logPath, "Main window activated.");
                    StartLifecycleTimer();
                    Log(logPath, "Startup complete.");
                }
                catch (Exception ex)
                {
                    Log(logPath, $"FATAL in XAML callback: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    try { File.WriteAllText(logPath + ".callback-fatal.txt", ex.ToString()); } catch { }
                    throw;
                }
            });
            return 0;
        }
        catch (Exception ex)
        {
            try { Log(logPath, $"FATAL at startup: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); } catch { }
            try { File.WriteAllText(logPath + ".fatal.txt", ex.ToString()); } catch { }
            return 0xDEAD;
        }
    }

    private static void StartLifecycleTimer()
    {
        try
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            if (dispatcher is null) return;
            var lifecycle = App.Services.GetRequiredService<TabLifecycleManager>();
            var tqTimer = dispatcher.CreateTimer();
            tqTimer.Interval = TimeSpan.FromSeconds(60);
            tqTimer.IsRepeating = true;
            tqTimer.Tick += (s, e) =>
            {
                _ = lifecycle.TickAsync();
            };
            tqTimer.Start();
        }
        catch { }
    }

    private static readonly object _logGate = new();
    private static void Log(string path, string line)
    {
        var stamp = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {line}";
        try
        {
            lock (_logGate)
            {
                File.AppendAllText(path, stamp + Environment.NewLine);
            }
        }
        catch { /* never throw from logging */ }
    }
}