using System.IO;
using System.Media;

namespace PrivacyToggle.Services;

/// <summary>Short built-in click sounds, synthesized once at startup.</summary>
public sealed class SoundService : IDisposable
{
    private readonly SoundPlayer _mute = new(BuildWave(880, 587));   // falling: mic off
    private readonly SoundPlayer _unmute = new(BuildWave(587, 880)); // rising: mic on

    public SoundService()
    {
        _mute.Load();
        _unmute.Load();
    }

    public void Play(bool muted)
    {
        try { (muted ? _mute : _unmute).Play(); } catch { /* no output device: ignore */ }
    }

    private static MemoryStream BuildWave(params double[] tones)
    {
        const int rate = 44100;
        const double toneSec = 0.055, volume = 0.25;
        int perTone = (int)(rate * toneSec);
        int samples = perTone * tones.Length;

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2);

        foreach (var freq in tones)
        {
            for (int i = 0; i < perTone; i++)
            {
                // short attack/release envelope to avoid clicks
                double env = Math.Min(1.0, Math.Min(i, perTone - i) / (rate * 0.006));
                double v = Math.Sin(2 * Math.PI * freq * i / rate) * env * volume;
                w.Write((short)(v * short.MaxValue));
            }
        }
        w.Flush();
        ms.Position = 0;
        return ms;
    }

    public void Dispose()
    {
        _mute.Dispose();
        _unmute.Dispose();
    }
}
