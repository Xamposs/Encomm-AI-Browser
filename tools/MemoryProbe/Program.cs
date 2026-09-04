using System.Diagnostics;
using Encomm.Browser.Memory;

namespace Encomm.Tools.MemoryProbe;

internal static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("Encomm MemoryProbe — single-process RSS sample.");
        var probe = new Encomm.Browser.Memory.MemoryProbe(() => System.Array.Empty<Encomm.Browser.Memory.TabStateSummary>());
        var s = probe.Sample();
        Console.WriteLine($"Working set:  {Format(s.WorkingSetBytes)}");
        Console.WriteLine($"Private:      {Format(s.PrivateBytes)}");
        Console.WriteLine($"PID:          {Environment.ProcessId}");
        Console.WriteLine($"Process:      {Process.GetCurrentProcess().ProcessName}");
        Console.WriteLine($"Sampled UTC:  {s.SampledUtc:o}");
        return 0;
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