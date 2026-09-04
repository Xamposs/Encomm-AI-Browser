using System.Timers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Encomm.Browser.App.Services;
using Encomm.Browser.Memory;
using Encomm.Browser.Shield;

namespace Encomm.Browser.App.Dialogs;

public sealed partial class MemoryPanelDialog : ContentDialog
{
    private readonly TabService _tabs;
    private readonly MemoryProbe _probe;
    private readonly IRequestBlocker _shield;
    private readonly BrowserRuntime _runtime;
    private readonly System.Timers.Timer _timer;

    public MemoryPanelDialog()
    {
        InitializeComponent();
        _tabs = App.Services.GetRequiredService<TabService>();
        _probe = App.Services.GetRequiredService<MemoryProbe>();
        _shield = App.Services.GetRequiredService<IRequestBlocker>();
        _runtime = App.Services.GetRequiredService<BrowserRuntime>();
        _timer = new System.Timers.Timer(1000) { AutoReset = true };
        _timer.Elapsed += (_, _) => DispatcherQueue.TryEnqueue(Refresh);
        Opened += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        var infos = _runtime.GetWebViewProcessInfos();
        var sample = _probe.Sample(infos);
        MemorySummary.Text = $"Host WS:    {Format(sample.HostWorkingSetBytes)}\n" +
                             $"Host priv:  {Format(sample.HostPrivateBytes)}\n" +
                             $"WV2 browser:{Format(sample.WebView2BrowserBytes)}\n" +
                             $"WV2 render: {Format(sample.WebView2RendererBytes)}\n" +
                             $"WV2 gpu:    {Format(sample.WebView2GpuBytes)}\n" +
                             $"WV2 util:   {Format(sample.WebView2UtilityBytes)}\n" +
                             $"Tree total: {Format(sample.ProcessTreeBytes)} ({sample.ProcessCount} procs)\n" +
                             $"Process:    {System.Diagnostics.Process.GetCurrentProcess().ProcessName}";
        TabSummary.Text = $"Tabs: {sample.TotalTabs} total | Live: {sample.LiveTabs} | Warm: {sample.WarmTabs} | Ghost: {sample.GhostTabs}";
        var s = _shield.Stats;
        ShieldSummary.Text = $"Shield: {s.BlockedRequests}/{s.TotalRequests} blocked ({s.BlockedAds} ads / {s.BlockedTrackers} trackers)";
        var items = new System.Collections.Generic.List<object>();
        foreach (var t in _tabs.Tabs)
        {
            items.Add(new
            {
                Title = t.Title,
                Url = t.Url,
                RendererState = t.RendererState.ToString()
            });
        }
        TabList.ItemsSource = items;
    }

    private static string Format(long bytes)
    {
        double v = bytes;
        string[] suf = { "B", "KB", "MB", "GB" };
        int i = 0;
        while (v >= 1024 && i < suf.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {suf[i]}";
    }
}