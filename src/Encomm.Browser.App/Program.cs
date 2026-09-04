using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Encomm.Browser.App.Services;
using Encomm.Browser.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace Encomm.Browser.App;

/// <summary>
/// Custom entry point.
///
/// We do our own bootstrap so we can write a startup log before XAML
/// touches anything, surface failures as a real exit code, and run
/// without depending on the Windows App SDK framework package being
/// properly registered for the current user (which is the dominant
/// reason WinUI 3 unpackaged apps fail on dev machines that have the
/// runtime installed by another tool but not registered for the user).
///
/// The startup log is written to %LOCALAPPDATA%\Encomm\Encomm-AI-Browser\Logs\encomm.log
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

            // 1) Search the executable directory first so the WinAppRuntime
            //    DLLs in self-contained builds are found without the
            //    framework package.
            SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS |
                                     LOAD_LIBRARY_SEARCH_APPLICATION_DIR |
                                     LOAD_LIBRARY_SEARCH_USER_DIRS |
                                     LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
            AddDllDirectory(AppContext.BaseDirectory);
            Log(logPath, $"DLL search path: {AppContext.BaseDirectory}");

            // 2) Force-load the Windows App Runtime native DLL. This is the
            //    same call the SDK's UndockedRegFreeWinRT auto-initializer
            //    makes, and it is what allows `Microsoft.UI.Xaml` etc. to be
            //    activated from the bin directory in unpackaged / framework-
            //    dependent mode.
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

            // 4) Try the bootstrap as a last-ditch effort. In self-contained
            //    mode the bootstrap may legitimately fail (it looks for a
            //    framework package by default); we try the OnPackageIdentity_NOOP
            //    option so it does not look for a package.
            TryBootstrap(logPath, 0x00020002, (string?)null, "WinAppSDK 2.2 release");
            TryBootstrap(logPath, 0x00010007, (string?)null, "WinAppSDK 1.7 release");

            // 5) Start XAML application on the UI thread.
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
            tqTimer.Tick += (s, e) => { try { lifecycle.Tick(); } catch { } };
            tqTimer.Start();
        }
        catch { }
    }

    private static void TryBootstrap(string logPath, uint majorMinor, string? versionTag, string label)
    {
        try
        {
            var pkgVersion = new PackageVersion();
            var options = Bootstrap.InitializeOptions.OnPackageIdentity_NOOP;
            bool ok = Bootstrap.TryInitialize(majorMinor, versionTag, pkgVersion, options, out int hr);
            Log(logPath, $"Bootstrap ({label}) ok={ok} hr=0x{hr:X8} options=OnPackageIdentity_NOOP");
        }
        catch (DllNotFoundException dnf)
        {
            Log(logPath, $"Bootstrap DLL missing ({label}): {dnf.Message}");
        }
        catch (Exception ex)
        {
            Log(logPath, $"Bootstrap EXCEPTION ({label}): {ex.GetType().Name}: {ex.Message}");
        }
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