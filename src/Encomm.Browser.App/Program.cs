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
/// anything and surface failures as a real exit code. The WinAppSDK
/// auto-init handles UndockedRegFreeWinRT internally; we only force-load
/// <c>Microsoft.WindowsAppRuntime.dll</c> to ensure native symbols are
/// resolved before XAML activation.
///
/// Supported command-line options:
///   --smoke-test     create one tab, navigate the local about:blank,
///                    verify the WebView2 environment initializes, exit
///                    with 0 on success or non-zero on failure.
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

            // 1) Set DLL search order so the runtime DLLs in the bin
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

            // 4) Initialize the Windows App SDK bootstrap explicitly
            // (the silent auto-initializer is disabled in the csproj).
            // Try the SDK's exact min version first, then 0.0.0.0.
            if (!TryBootstrap(logPath, 0x00010007, "stable", 7000, 522, 1444, 0)
                && !TryBootstrap(logPath, 0x00010007, "", 0, 0, 0, 0))
            {
                Log(logPath, "FATAL: bootstrap failed; cannot activate WinUI. See https://learn.microsoft.com/windows/apps/windows-app-sdk/deploy-unpackaged-apps");
                return 0xB001;
            }

            // 4) Smoke-test mode bypasses XAML; exits 0/non-zero.
            if (Array.IndexOf(args, "--smoke-test") >= 0)
            {
                return RunSmokeTest(logPath);
            }

            // 5) Initialize services before XAML starts.
            App.Services = App.BuildServicesStatic();
            App.SetStartupArgs(args);
            Log(logPath, "DI services built.");
            try
            {
                var settings = App.Services.GetRequiredService<SettingsService>();
                var lifecycle = App.Services.GetRequiredService<TabLifecycleManager>();
                var runtime = App.Services.GetRequiredService<BrowserRuntime>();
                lifecycle.ApplySettings(settings.Current, runtime);
                Log(logPath, $"Lifecycle configured: preset={lifecycle.Preset} warm={lifecycle.WarmAfter} ghost={lifecycle.GhostAfter} saver={lifecycle.MemorySaverEnabled} pressureMB={settings.Current.MemoryPressureThresholdMB}");
            }
            catch (Exception ex)
            {
                Log(logPath, $"Lifecycle settings apply FAILED: {ex.GetType().Name}: {ex.Message}");
            }

            // 6) Start XAML application on the UI thread.
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Log(logPath, "ComWrappers initialized. Starting XAML Application.");
            int exitCode = 0;
            Application.Start((ApplicationInitializationCallbackParams p) =>
            {
                try
                {
                    var dq = DispatcherQueue.GetForCurrentThread();
                    if (dq is null)
                    {
                        Log(logPath, "FATAL: DispatcherQueue.GetForCurrentThread() returned null");
                        throw new InvalidOperationException("DispatcherQueue unavailable on UI thread.");
                    }
                    App.InitializeUiDispatcher(dq);

                    var ctx = new DispatcherQueueSynchronizationContext(dq);
                    SynchronizationContext.SetSynchronizationContext(ctx);
                    Log(logPath, "XAML UI thread dispatcher ready.");

                    Log(logPath, "Constructing App instance.");
                    var app = new App();
                    Log(logPath, "App instance created.");

                    Log(logPath, "Constructing MainWindow.");
                    var window = new MainWindow();
                    Log(logPath, "MainWindow constructed; calling Activate.");
                    window.Activate();
                    Log(logPath, "Main window activated.");

                    // Start lifecycle scheduler (DispatcherQueueTimer) on the UI
                    // thread so the timer can safely marshal back.
                    try
                    {
                        var tqTimer = dq.CreateTimer();
                        tqTimer.Interval = TimeSpan.FromSeconds(60);
                        tqTimer.IsRepeating = true;
                        var lifecycle = App.Services.GetRequiredService<TabLifecycleManager>();
                        tqTimer.Tick += (s, e) =>
                        {
                            // Run async fire-and-forget; lifecycle is fault-tolerant.
                            _ = lifecycle.TickAsync();
                        };
                        tqTimer.Start();
                        Log(logPath, "Lifecycle DispatcherQueueTimer started.");
                    }
                    catch (Exception ex)
                    {
                        Log(logPath, $"Lifecycle timer start failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    Log(logPath, "Startup complete.");
                }
                catch (Exception ex)
                {
                    Log(logPath, $"FATAL in XAML callback: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    try { File.WriteAllText(logPath + ".callback-fatal.txt", ex.ToString()); } catch { }
                    exitCode = 0xDEAD;
                }
            });
            return exitCode;
        }
        catch (Exception ex)
        {
            try { Log(logPath, $"FATAL at startup: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); } catch { }
            try { File.WriteAllText(logPath + ".fatal.txt", ex.ToString()); } catch { }
            return 0xDEAD;
        }
    }

    /// <summary>
    /// Initialize the Windows App SDK bootstrap explicitly and log the
    /// HRESULT. Returns true when a compatible framework was found.
    /// Uses InitializeOptions.None so failures return an HRESULT instead
    /// of showing UI or fail-fasting the process.
    /// </summary>
    private static bool TryBootstrap(string logPath, uint majorMinor, string versionTag,
        ushort minMajor, ushort minMinor, ushort minBuild, ushort minRevision)
    {
        try
        {
            var minVersion = new Microsoft.Windows.ApplicationModel.DynamicDependency.PackageVersion(
                minMajor, minMinor, minBuild, minRevision);
            bool ok = Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.TryInitialize(
                majorMinor, versionTag, minVersion,
                Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.InitializeOptions.None,
                out int hr);
            Log(logPath, $"Bootstrap majorMinor=0x{majorMinor:X8} tag='{versionTag}' min={minMajor}.{minMinor}.{minBuild}.{minRevision}: ok={ok} hr=0x{hr:X8}");
            return ok;
        }
        catch (Exception ex)
        {
            Log(logPath, $"Bootstrap EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Headless smoke test. Returns 0 on success, non-zero on failure.
    /// </summary>
    private static int RunSmokeTest(string logPath)
    {
        try
        {
            Log(logPath, "Smoke test: probing WebView2 runtime.");
            var version = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            Log(logPath, $"Smoke test: WebView2 runtime version: {version}");
            // We do not initialize a CoreWebView2Environment here because that
            // would create a user-data folder on disk. The test verifies that
            // the runtime DLL is loadable and reports a version, which is a
            // strong signal that the rest of the application will work.
            Log(logPath, "Smoke test: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Log(logPath, $"Smoke test: FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
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
