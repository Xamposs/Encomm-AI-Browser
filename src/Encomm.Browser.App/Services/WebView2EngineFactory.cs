using Microsoft.Extensions.Logging;
using Encomm.Browser.App.Services;
using Encomm.Browser.Engine.Abstractions;
using Encomm.Browser.Engine.WebView2;
using Encomm.Browser.Shield;

namespace Encomm.Browser.App.Services;

/// <summary>
/// Builds the single shared WebView2 engine. The runtime DLLs are
/// loaded by the WinAppSDK 1.7 UndockedRegFreeWinRT mechanism
/// (force-loaded from the application directory in Program.Main);
/// we just hand a fresh engine to the BrowserRuntime, which owns the
/// single shared CoreWebView2Environment created by the adapter.
/// </summary>
public sealed class WebView2EngineFactory
{
    private readonly IRequestBlocker _blocker;
    private readonly ILogger<BrowserRuntime> _log;

    public WebView2EngineFactory(IRequestBlocker blocker, ILogger<BrowserRuntime> log)
    {
        _blocker = blocker;
        _log = log;
    }

    public async Task<IBrowserEngine> CreateAsync(BrowserRuntime runtime, CancellationToken ct = default)
    {
        _log.LogInformation("Initializing WebView2 engine.");
        // Defer environment creation to the WebView2 adapter which has the
        // correct net6.0-windows10.0.17763.0 projection available.
        return await Task.FromResult(new WebView2Engine(_blocker, runtime.Ui)).ConfigureAwait(false);
    }
}