using NAudio.Wave;
using RemoteConnect.Core;

namespace RemoteConnect.Host;

/// <summary>Captures what the host is playing (WASAPI loopback) and emits 16-bit PCM chunks.</summary>
public sealed class AudioCapture : IDisposable
{
    private readonly WasapiLoopbackCapture _capture;
    private readonly Action<int, int, byte[]> _sink;

    public AudioCapture(Action<int, int, byte[]> sink)
    {
        _sink = sink;
        _capture = new WasapiLoopbackCapture();
        _capture.DataAvailable += OnData;
        _capture.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        var fmt = _capture.WaveFormat;
        byte[] pcm;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat || (fmt is WaveFormatExtensible && fmt.BitsPerSample == 32))
        {
            int samples = e.BytesRecorded / 4;
            pcm = new byte[samples * 2];
            var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, samples * 4));
            var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
            for (int i = 0; i < samples; i++)
                dst[i] = (short)(Math.Clamp(src[i], -1f, 1f) * 32767f);
        }
        else if (fmt.BitsPerSample == 16)
        {
            pcm = e.Buffer.AsSpan(0, e.BytesRecorded).ToArray();
        }
        else return;
        try { _sink(fmt.SampleRate, fmt.Channels, pcm); } catch (Exception ex) { Log.Error("audio sink", ex); }
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
    }
}
