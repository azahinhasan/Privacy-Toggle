using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PrivacyToggle.Services;

/// <summary>
/// The computer's current download and upload speed, from Windows' byte counters of its real network
/// adapters (no admin rights or helper needed). Only hardware interfaces count: virtual ones (VPN, Hyper-V)
/// and Windows' filter copies of each adapter carry the same bytes again and would double the speed.
/// </summary>
public sealed class NetSpeedMeter
{
    /// <summary>Speeds average this long, by the clock rather than by how many samples arrived.</summary>
    private const double Window = 3;

    /// <summary>Below this the latest second counts as stopped, and the speed shows that right away.</summary>
    private const double Idle = 1024;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Queue<(double Time, long In, long Out)> _samples = new();

    /// <summary>
    /// Bytes per second down and up: total bytes over the last ~3 seconds divided by the exact time they took, so a
    /// steady download reads steady even if this is called a little early or late. If the latest interval had
    /// (almost) nothing, that's returned instead, so a stopped download reads 0 at once. The first call returns 0, 0.
    /// </summary>
    public (double Down, double Up) Sample()
    {
        var (bytesIn, bytesOut) = Totals();
        double now = _clock.Elapsed.TotalSeconds;

        // Counters drop when an adapter is turned off or unplugged: start over rather than show a negative speed.
        if (_samples.Count > 0 && (bytesIn < _samples.Last().In || bytesOut < _samples.Last().Out)) _samples.Clear();
        _samples.Enqueue((now, bytesIn, bytesOut));
        while (_samples.Count > 2 && now - _samples.Peek().Time > Window + 0.5) _samples.Dequeue();
        if (_samples.Count < 2) return (0, 0);

        var all = _samples.ToArray();
        var (prevTime, prevIn, prevOut) = all[^2];
        double step = now - prevTime;
        if (step > 0 && (bytesIn - prevIn + bytesOut - prevOut) / step < Idle)
            return ((bytesIn - prevIn) / step, (bytesOut - prevOut) / step);

        var (firstTime, firstIn, firstOut) = all[0];
        double span = now - firstTime;
        return span <= 0 ? (0, 0) : ((bytesIn - firstIn) / span, (bytesOut - firstOut) / span);
    }

    /// <summary>Total bytes received and sent by the hardware network adapters that are up.</summary>
    private static (long In, long Out) Totals()
    {
        if (GetIfTable2(out IntPtr table) != 0) return (0, 0);
        try
        {
            long totalIn = 0, totalOut = 0;
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                // MIB_IF_ROW2 (1352 bytes on x64); rows start after the 8-byte count.
                IntPtr row = table + 8 + i * RowSize;
                byte flags = Marshal.ReadByte(row, 1152);          // InterfaceAndOperStatusFlags
                bool hardware = (flags & 0x1) != 0, filter = (flags & 0x2) != 0;
                if (!hardware || filter || Marshal.ReadInt32(row, 1156) != IfOperStatusUp) continue;
                totalIn += Marshal.ReadInt64(row, 1208);           // InOctets
                totalOut += Marshal.ReadInt64(row, 1280);          // OutOctets
            }
            return (totalIn, totalOut);
        }
        finally { FreeMibTable(table); }
    }

    private const int RowSize = 1352, IfOperStatusUp = 1;

    [DllImport("iphlpapi.dll")]
    private static extern int GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr table);
}
