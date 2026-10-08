using Microsoft.Win32;
using Pollux.App.Interop;

namespace Pollux.App.Services;

internal readonly record struct StatsSnapshot(double Cpu, double? Gpu, double? VramUsedGb, double? VramTotalGb, double RamUsedGb, double RamTotalGb);

/// <summary>CPU, GPU, VRAM and RAM usage from Win32 and performance counters. No admin needed.</summary>
internal sealed unsafe class SystemStats : IDisposable
{
    private ulong _lastIdle, _lastTotal;
    private nint _query, _gpuCounter, _vramCounter;
    private readonly double? _vramTotalGb = ReadVramTotalGb();

    public SystemStats()
    {
        if (Native.PdhOpenQueryW(null, 0, out _query) == 0)
        {
            if (Native.PdhAddEnglishCounterW(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", 0, out _gpuCounter) != 0) _gpuCounter = 0;
            if (Native.PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out _vramCounter) != 0) _vramCounter = 0;
            Native.PdhCollectQueryData(_query);
        }

        Sample();
    }

    public StatsSnapshot Sample()
    {
        var cpu = 0.0;
        if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel.Value + user.Value;
            var dTotal = total - _lastTotal;
            var dIdle = idle.Value - _lastIdle;
            cpu = dTotal == 0 ? 0 : Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
            _lastTotal = total;
            _lastIdle = idle.Value;
        }

        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)sizeof(Native.MEMORYSTATUSEX) };
        Native.GlobalMemoryStatusEx(ref mem);

        double? gpu = null, vram = null;
        if (_query != 0 && Native.PdhCollectQueryData(_query) == 0)
        {
            if (_gpuCounter != 0) gpu = Math.Clamp(SumDouble(_gpuCounter), 0, 100);
            if (_vramCounter != 0) vram = SumLarge(_vramCounter) / 1024d / 1024 / 1024;
        }

        return new StatsSnapshot(cpu, gpu, vram, _vramTotalGb, (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824d, mem.ullTotalPhys / 1073741824d);
    }

    public static (double FreeGb, double TotalGb)? Disk(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return null;
        return Native.GetDiskFreeSpaceExW(root, out var free, out var total, out _)
            ? (free / 1073741824d, total / 1073741824d)
            : null;
    }

    private static double SumDouble(nint counter)
    {
        uint size = 0;
        if (Native.PdhGetFormattedCounterArrayW(counter, Native.PDH_FMT_DOUBLE, ref size, out _, null) != Native.PDH_MORE_DATA || size == 0) return 0;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (Native.PdhGetFormattedCounterArrayW(counter, Native.PDH_FMT_DOUBLE, ref size, out var count, p) != 0) return 0;
            var items = (Native.PDH_FMT_COUNTERVALUE_ITEM_W*)p;
            double sum = 0;
            for (var i = 0; i < count; i++) sum += items[i].Value;
            return sum;
        }
    }

    private static double SumLarge(nint counter)
    {
        uint size = 0;
        if (Native.PdhGetFormattedCounterArrayW(counter, Native.PDH_FMT_LARGE, ref size, out _, null) != Native.PDH_MORE_DATA || size == 0) return 0;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (Native.PdhGetFormattedCounterArrayW(counter, Native.PDH_FMT_LARGE, ref size, out var count, p) != 0) return 0;
            var items = (Native.PDH_FMT_COUNTERVALUE_ITEM_LARGE*)p;
            double sum = 0;
            for (var i = 0; i < count; i++) sum += items[i].Value;
            return sum;
        }
    }

    /// <summary>Dedicated VRAM of the first real display adapter, from the driver's registry key (readable without admin).</summary>
    private static double? ReadVramTotalGb()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return null;
            double best = 0;
            foreach (var name in cls.GetSubKeyNames())
            {
                if (name.Length != 4) continue;
                using var k = cls.OpenSubKey(name);
                var v = k?.GetValue("HardwareInformation.qwMemorySize");
                var bytes = v switch { long l => l, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b), _ => 0L };
                best = Math.Max(best, bytes / 1073741824d);
            }

            return best > 0 ? best : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_query != 0)
        {
            Native.PdhCloseQuery(_query);
            _query = 0;
        }
    }
}
