using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Encomm.Browser.Memory;

public sealed record MemorySample(
    long WorkingSetBytes,
    long PrivateBytes,
    int LiveTabs,
    int WarmTabs,
    int GhostTabs,
    int TotalTabs,
    DateTimeOffset SampledUtc);

/// <summary>
/// Lightweight in-process memory probe. No allocations beyond the
/// sample itself on every poll. Designed to be called from a timer or
/// on-demand from the UI.
/// </summary>
public sealed class MemoryProbe
{
    private readonly Func<IReadOnlyList<TabStateSummary>> _tabSource;
    private readonly Process _process;

    public MemoryProbe(Func<IReadOnlyList<TabStateSummary>> tabSource)
    {
        _tabSource = tabSource;
        _process = Process.GetCurrentProcess();
    }

    public MemorySample Sample()
    {
        try { _process.Refresh(); } catch { }
        var ws = _process.WorkingSet64;
        long privateBytes = 0;
        try { privateBytes = _process.PrivateMemorySize64; } catch { }
        var tabs = _tabSource();
        int live = 0, warm = 0, ghost = 0;
        foreach (var t in tabs)
        {
            switch (t.RendererState)
            {
                case TabRendererState.Live: live++; break;
                case TabRendererState.Warm: warm++; break;
                case TabRendererState.Ghost: ghost++; break;
            }
        }
        return new MemorySample(ws, privateBytes, live, warm, ghost, tabs.Count, DateTimeOffset.UtcNow);
    }
}

public enum TabRendererState { Live, Warm, Ghost }

public sealed record TabStateSummary(Guid Id, TabRendererState RendererState);

/// <summary>
/// Optional Win32 helper for higher-fidelity private working-set info.
/// Falls back gracefully when the API is not present.
/// </summary>
public static class NativeMemory
{
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS counters, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }

    public static long GetPrivateWorkingSetBytes()
    {
        try
        {
            var p = Process.GetCurrentProcess();
            if (GetProcessMemoryInfo(p.Handle, out var c, (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>()))
                return (long)c.WorkingSetSize;
        }
        catch { }
        return 0;
    }
}