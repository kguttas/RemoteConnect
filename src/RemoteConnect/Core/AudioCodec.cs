using FFmpeg.AutoGen;

namespace RemoteConnect.Core;

/// <summary>Receives each encoded packet straight from FFmpeg's buffer (valid only during the call).</summary>
public delegate void PacketSink(ReadOnlySpan<byte> data, bool key);

/// <summary>
/// Opus for the host's audio: 128 kb/s stereo in 10 ms frames instead of ~1.5 Mb/s of raw PCM, so audio
/// costs next to nothing on a slow link. Input is any rate / channel count of s16 PCM (resampled to 48 kHz stereo).
/// </summary>
public sealed unsafe class OpusEncoder : IDisposable
{
    public const int Rate = 48000, Channels = 2;

    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private SwrContext* _swr;
    private int _inRate, _inChannels;
    private short[] _fifo = new short[Rate * Channels];
    private int _fifoSamples; // per channel
    private long _pts;

    public static OpusEncoder? TryCreate()
    {
        try { return new OpusEncoder(); }
        catch (Exception ex) { Log.Write("Opus unavailable, sending PCM: " + ex.Message); return null; }
    }

    private OpusEncoder()
    {
        FFmpegSetup.Init();
        var codec = ffmpeg.avcodec_find_encoder_by_name("libopus");
        if (codec == null) throw new InvalidOperationException("libopus no está en esta compilación de FFmpeg");
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->sample_rate = Rate;
        ffmpeg.av_channel_layout_default(&_ctx->ch_layout, Channels);
        _ctx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
        _ctx->bit_rate = 128_000;
        _ctx->time_base = new AVRational { num = 1, den = Rate };
        AVDictionary* opts = null;
        ffmpeg.av_dict_set(&opts, "application", "lowdelay", 0);
        ffmpeg.av_dict_set(&opts, "frame_duration", "10", 0);
        int r = ffmpeg.avcodec_open2(_ctx, codec, &opts);
        ffmpeg.av_dict_free(&opts);
        if (r < 0)
        {
            var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null;
            throw new InvalidOperationException("libopus: " + FFmpegSetup.ErrorText(r));
        }
        _frame = ffmpeg.av_frame_alloc();
        _frame->nb_samples = _ctx->frame_size;
        _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_S16;
        _frame->sample_rate = Rate;
        ffmpeg.av_channel_layout_copy(&_frame->ch_layout, &_ctx->ch_layout);
        ffmpeg.av_frame_get_buffer(_frame, 0);
        _pkt = ffmpeg.av_packet_alloc();
    }

    /// <summary>Feeds interleaved s16 PCM; emits one Opus packet per 10 ms of audio.</summary>
    public void Encode(int rate, int channels, ReadOnlySpan<byte> pcm, PacketSink sink)
    {
        int inSamples = pcm.Length / 2 / channels;
        if (inSamples == 0) return;
        if (rate == Rate && channels == Channels && _swr == null)
        {
            Append(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm));
        }
        else
        {
            if (_swr == null || rate != _inRate || channels != _inChannels) CreateResampler(rate, channels);
            int outCap = (int)((long)inSamples * Rate / rate) + 256;
            var tmp = new short[outCap * Channels];
            int got;
            fixed (byte* src = pcm)
            fixed (short* dst = tmp)
            {
                byte* inPlane = src;
                byte* outPlane = (byte*)dst;
                got = ffmpeg.swr_convert(_swr, &outPlane, outCap, &inPlane, inSamples);
            }
            if (got > 0) Append(tmp.AsSpan(0, got * Channels));
        }

        int frameSize = _ctx->frame_size;
        while (_fifoSamples >= frameSize)
        {
            ffmpeg.av_frame_make_writable(_frame);
            new ReadOnlySpan<short>(_fifo, 0, frameSize * Channels).CopyTo(new Span<short>(_frame->data[0], frameSize * Channels));
            Array.Copy(_fifo, frameSize * Channels, _fifo, 0, (_fifoSamples - frameSize) * Channels);
            _fifoSamples -= frameSize;
            _frame->pts = _pts;
            _pts += frameSize;
            if (ffmpeg.avcodec_send_frame(_ctx, _frame) < 0) return;
            while (ffmpeg.avcodec_receive_packet(_ctx, _pkt) == 0)
            {
                try { sink(new ReadOnlySpan<byte>(_pkt->data, _pkt->size), false); }
                finally { ffmpeg.av_packet_unref(_pkt); }
            }
        }
    }

    private void Append(ReadOnlySpan<short> samples)
    {
        int need = _fifoSamples * Channels + samples.Length;
        if (need > _fifo.Length)
        {
            // More than a second behind: the capture side stalled; drop the backlog rather than add latency.
            if (need > Rate * Channels * 2) { _fifoSamples = 0; need = samples.Length; }
            if (need > _fifo.Length) Array.Resize(ref _fifo, need);
        }
        samples.CopyTo(_fifo.AsSpan(_fifoSamples * Channels));
        _fifoSamples += samples.Length / Channels;
    }

    private void CreateResampler(int rate, int channels)
    {
        if (_swr != null) { var old = _swr; ffmpeg.swr_free(&old); _swr = null; }
        AVChannelLayout inLayout, outLayout;
        ffmpeg.av_channel_layout_default(&inLayout, channels);
        ffmpeg.av_channel_layout_default(&outLayout, Channels);
        SwrContext* swr = null;
        ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, Rate,
            &inLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, rate, 0, null);
        if (swr == null || ffmpeg.swr_init(swr) < 0) throw new InvalidOperationException("swr_init");
        _swr = swr;
        _inRate = rate;
        _inChannels = channels;
    }

    public void Dispose()
    {
        if (_ctx == null) return;
        var f = _frame; ffmpeg.av_frame_free(&f);
        var p = _pkt; ffmpeg.av_packet_free(&p);
        var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null;
        if (_swr != null) { var s = _swr; ffmpeg.swr_free(&s); _swr = null; }
    }
}

/// <summary>Opus → interleaved s16 PCM at 48 kHz stereo.</summary>
public sealed unsafe class OpusDecoder : IDisposable
{
    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private SwrContext* _swr;

    public OpusDecoder()
    {
        FFmpegSetup.Init();
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_OPUS);
        if (codec == null) throw new InvalidOperationException("Sin decodificador Opus");
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->sample_rate = OpusEncoder.Rate;
        ffmpeg.av_channel_layout_default(&_ctx->ch_layout, OpusEncoder.Channels);
        int r = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (r < 0) throw new InvalidOperationException("Opus: " + FFmpegSetup.ErrorText(r));
        _frame = ffmpeg.av_frame_alloc();
        _pkt = ffmpeg.av_packet_alloc();
    }

    public byte[]? Decode(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            _pkt->data = p;
            _pkt->size = data.Length;
            int r = ffmpeg.avcodec_send_packet(_ctx, _pkt);
            _pkt->data = null;
            _pkt->size = 0;
            if (r < 0) return null;
        }
        byte[]? result = null;
        while (ffmpeg.avcodec_receive_frame(_ctx, _frame) == 0)
        {
            if (_swr == null)
            {
                AVChannelLayout outLayout;
                ffmpeg.av_channel_layout_default(&outLayout, OpusEncoder.Channels);
                SwrContext* swr = null;
                ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, OpusEncoder.Rate,
                    &_frame->ch_layout, (AVSampleFormat)_frame->format, _frame->sample_rate, 0, null);
                if (swr == null || ffmpeg.swr_init(swr) < 0) return null;
                _swr = swr;
            }
            int cap = _frame->nb_samples + 64;
            var pcm = new byte[cap * OpusEncoder.Channels * 2];
            int got;
            fixed (byte* dst = pcm)
            {
                byte* outPlane = dst;
                got = ffmpeg.swr_convert(_swr, &outPlane, cap, (byte**)&_frame->data, _frame->nb_samples);
            }
            if (got > 0)
            {
                var chunk = pcm.AsSpan(0, got * OpusEncoder.Channels * 2);
                result = result is null ? chunk.ToArray() : [.. result, .. chunk];
            }
        }
        return result;
    }

    public void Dispose()
    {
        if (_ctx == null) return;
        var f = _frame; ffmpeg.av_frame_free(&f);
        var p = _pkt; ffmpeg.av_packet_free(&p);
        var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null;
        if (_swr != null) { var s = _swr; ffmpeg.swr_free(&s); _swr = null; }
    }
}
