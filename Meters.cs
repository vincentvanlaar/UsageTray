using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace UsageTray;

/// <summary>
/// CPU and GPU load. Both are rates, so each reading compares with the one before it:
/// the first reading after a pause covers that whole pause, and the very first has nothing to compare with.
/// </summary>
static class Meters
{
    static long _idle, _kernel, _user;

    /// <summary>Whole-machine CPU use in percent since the previous call; null on the first.</summary>
    public static double? Cpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var first = _kernel == 0;
        var (busy, total) = (kernel - _kernel + user - _user - (idle - _idle), kernel - _kernel + user - _user); // kernel time includes idle
        (_idle, _kernel, _user) = (idle, kernel, user);
        return first || total <= 0 ? null : Math.Clamp(100.0 * busy / total, 0, 100);
    }

    static readonly object GpuLock = new();
    static nint _query, _counter;
    static bool _noGpu;

    /// <summary>False once the GPU counters turned out to be missing: no GPU reading will come.</summary>
    public static bool HasGpu => !_noGpu;
    static DateTime _gpuAt;
    static GpuLoad? _gpu;

    /// <param name="Total">The busiest engine, as Task Manager shows it.</param>
    /// <param name="ByPid">Per process, its busiest engine.</param>
    public sealed record GpuLoad(double Total, IReadOnlyDictionary<int, double> ByPid);

    /// <summary>GPU use since the previous call (PDH "GPU Engine" counters); null on the first, or without a GPU.</summary>
    public static GpuLoad? Gpu()
    {
        lock (GpuLock)
        {
            // The flyout and the session panel read close together; share one reading.
            if (DateTime.UtcNow - _gpuAt < TimeSpan.FromMilliseconds(500)) return _gpu;
            _gpuAt = DateTime.UtcNow;
            if (_noGpu) return null;
            if (_query == 0)
            {
                if (PdhOpenQuery(null, 0, out _query) != 0
                    || PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _counter) != 0)
                    _noGpu = true;
                else PdhCollectQueryData(_query);
                return _gpu = null;
            }
            return _gpu = PdhCollectQueryData(_query) == 0 ? ReadGpu() : null;
        }
    }

    static GpuLoad? ReadGpu()
    {
        const uint Flags = 0x200 | 0x8000; // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100
        uint size = 0;
        if (PdhGetFormattedCounterArray(_counter, Flags, ref size, out _, 0) != 0x800007D2 /* PDH_MORE_DATA */) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, Flags, ref size, out var count, buffer) != 0) return null;
            var engines = new Dictionary<string, double>();
            var procs = new Dictionary<(int Pid, string Engine), double>();
            var itemSize = Marshal.SizeOf<CounterItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * itemSize);
                if (item.Status > 1) continue; // PDH_CSTATUS_VALID_DATA or _NEW_DATA
                // e.g. pid_1234_luid_0x00000000_0x0000D1A5_phys_0_eng_3_engtype_VideoDecode
                var m = Regex.Match(Marshal.PtrToStringUni(item.Name) ?? "", @"^pid_(\d+)_(luid_.+)_engtype_");
                if (!m.Success) continue;
                var engine = m.Groups[2].Value;
                engines[engine] = engines.GetValueOrDefault(engine) + item.Value;
                var key = (int.Parse(m.Groups[1].Value), engine);
                procs[key] = procs.GetValueOrDefault(key) + item.Value;
            }
            return new(Math.Min(100, engines.Values.DefaultIfEmpty(0).Max()),
                procs.GroupBy(p => p.Key.Pid).ToDictionary(g => g.Key, g => Math.Min(100, g.Max(p => p.Value))));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CounterItem // PDH_FMT_COUNTERVALUE_ITEM_W with a double value
    {
        public nint Name;
        public uint Status;
        public double Value;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    static extern uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    static extern uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint buffer);
}
