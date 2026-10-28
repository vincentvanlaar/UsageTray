using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UsageTray;

/// <summary>
/// Physical RAM and commit charge of this machine, and optionally CPU and GPU load, shown as an
/// extra flyout section.
/// </summary>
static class SystemSource
{
    static readonly string Name = L.T("System", "Systeem");

    /// <returns>null when the quick settings hide every row.</returns>
    public static ProviderStatus? Read()
    {
        var rows = new List<UsageWindow>();
        if (Settings.ShowMemory)
        {
            var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref m)) return new(Name, [], Error: new Win32Exception().Message);
            rows.Add(Row("ram", m.TotalPhys - m.AvailPhys, m.TotalPhys));
            rows.Add(Row("commit", m.TotalPageFile - m.AvailPageFile, m.TotalPageFile));
        }
        if (Settings.ShowCpu) rows.Add(Load("cpu", Meters.Cpu()));
        if (Settings.ShowGpu) rows.Add(Load("gpu", Meters.Gpu()?.Total));
        return rows.Count == 0 ? null : new(Name, rows, Note: L.T("click for processes", "klik voor processen"));
    }

    /// <summary>
    /// The RAM row alone, for the tray icon and tooltip. Leaves the CPU meter alone: it measures
    /// since its previous call, so an extra reading here would blank the flyout's CPU row.
    /// </summary>
    /// <returns>null when the quick settings hide memory, or it can't be read.</returns>
    public static UsageWindow? Ram()
    {
        if (!Settings.ShowMemory) return null;
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref m) ? Row("ram", m.TotalPhys - m.AvailPhys, m.TotalPhys) : null;
    }

    // Until a second reading there is no rate yet: an empty bar with "…".
    static UsageWindow Load(string label, double? pct) => new(label, pct ?? 0, TimeSpan.Zero, null, pct is null ? "…" : "");

    static UsageWindow Row(string label, ulong used, ulong total)
    {
        const double GB = 1 << 30;
        return new(label, total == 0 ? 0 : 100.0 * used / total, TimeSpan.Zero, null, $"{used / GB:0}/{total / GB:0}G");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
