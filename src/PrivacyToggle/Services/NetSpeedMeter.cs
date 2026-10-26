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
    private long _lastIn, _lastOut;
    private readonly Stopwatch _clock = new();

    /// <summary>Bytes per second since the previous call (the first call returns 0, 0).</summary>
    public (double Down, double Up) Sample()
    {
        var (bytesIn, bytesOut) = Totals();
        double seconds = _clock.Elapsed.TotalSeconds;
        bool first = !_clock.IsRunning;
        _clock.Restart();

        // Counters drop when an adapter is turned off or unplugged; treat that as a fresh start, not negative speed.
        double down = first || seconds <= 0 || bytesIn < _lastIn ? 0 : (bytesIn - _lastIn) / seconds;
        double up = first || seconds <= 0 || bytesOut < _lastOut ? 0 : (bytesOut - _lastOut) / seconds;
        (_lastIn, _lastOut) = (bytesIn, bytesOut);
        return (down, up);
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
