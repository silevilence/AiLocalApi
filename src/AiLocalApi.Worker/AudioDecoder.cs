using FFmpeg.AutoGen;

namespace AiLocalApi.Worker;

internal static unsafe class AudioDecoder
{
    public static double Decode(string input, string output, int maxSeconds, CancellationToken token)
    {
        ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (!File.Exists(Path.Combine(ffmpeg.RootPath, "avcodec-63.dll"))) throw new FileNotFoundException("缺少 FFmpeg 9 共享库。请运行 scripts/setup-native.ps1 后重新执行 scripts/build.ps1。");
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
        if (ffmpeg.avcodec_version() >> 16 != 63) throw new InvalidOperationException("FFmpeg 9 (avcodec 63) shared libraries required; run setup-native.ps1.");
        AVFormatContext* format = null; AVCodecContext* codec = null; SwrContext* resampler = null;
        AVPacket* packet = ffmpeg.av_packet_alloc(); AVFrame* frame = ffmpeg.av_frame_alloc();
        AVDictionary* options = null;
        AVChannelLayout mono = default;
        try
        {
            ffmpeg.av_dict_set(&options, "protocol_whitelist", "file", 0);
            ffmpeg.av_dict_set(&options, "format_whitelist", "wav,mp3,mov,flac,ogg,matroska,webm", 0);
            Check(ffmpeg.avformat_open_input(&format, input, null, &options));
            Check(ffmpeg.avformat_find_stream_info(format, null));
            var stream = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            Check(stream);
            var parameters = format->streams[stream]->codecpar;
            var decoder = ffmpeg.avcodec_find_decoder(parameters->codec_id);
            if (decoder is null) throw new InvalidDataException("Audio codec is unavailable.");
            codec = ffmpeg.avcodec_alloc_context3(decoder);
            Check(ffmpeg.avcodec_parameters_to_context(codec, parameters));
            Check(ffmpeg.avcodec_open2(codec, decoder, null));
            if (codec->sample_rate <= 0) throw new InvalidDataException("Invalid audio sample rate.");
            ffmpeg.av_channel_layout_default(&mono, 1);
            Check(ffmpeg.swr_alloc_set_opts2(&resampler, &mono, AVSampleFormat.AV_SAMPLE_FMT_FLT, 16000, &codec->ch_layout, codec->sample_fmt, codec->sample_rate, 0, null));
            Check(ffmpeg.swr_init(resampler));
            using var target = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
            long samples = 0;
            int read;
            while ((read = ffmpeg.av_read_frame(format, packet)) >= 0)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (packet->stream_index != stream) continue;
                    Check(ffmpeg.avcodec_send_packet(codec, packet));
                    Drain(codec, frame, resampler, target, ref samples, maxSeconds);
                }
                finally { ffmpeg.av_packet_unref(packet); }
            }
            if (read != ffmpeg.AVERROR_EOF) Check(read);
            Check(ffmpeg.avcodec_send_packet(codec, null));
            Drain(codec, frame, resampler, target, ref samples, maxSeconds);
            ConvertSamples(resampler, null, 0, target, ref samples, maxSeconds);
            if (samples == 0) throw new InvalidDataException("Audio contains no samples.");
            return samples / 16000d;
        }
        finally
        {
            ffmpeg.av_dict_free(&options); ffmpeg.av_channel_layout_uninit(&mono);
            ffmpeg.swr_free(&resampler); ffmpeg.av_frame_free(&frame); ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&codec); ffmpeg.avformat_close_input(&format);
        }
    }
    private static void Drain(AVCodecContext* codec, AVFrame* frame, SwrContext* resampler, Stream output, ref long samples, int maxSeconds)
    {
        int result;
        while ((result = ffmpeg.avcodec_receive_frame(codec, frame)) >= 0)
        {
            try { ConvertSamples(resampler, frame->extended_data, frame->nb_samples, output, ref samples, maxSeconds); }
            finally { ffmpeg.av_frame_unref(frame); }
        }
        if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN) && result != ffmpeg.AVERROR_EOF) Check(result);
    }
    private static void ConvertSamples(SwrContext* resampler, byte** input, int count, Stream output, ref long samples, int maxSeconds)
    {
        var capacity = ffmpeg.swr_get_out_samples(resampler, count);
        Check(capacity);
        var data = new float[Math.Max(1, capacity)];
        fixed (float* buffer = data)
        {
            var pointer = (byte*)buffer;
            var produced = ffmpeg.swr_convert(resampler, &pointer, capacity, input, count); Check(produced);
            samples += produced;
            if (samples > maxSeconds * 16000L) throw new InvalidDataException("Decoded audio exceeds configured duration limit.");
            output.Write(new ReadOnlySpan<byte>(buffer, produced * sizeof(float)));
        }
    }
    private static void Check(int result)
    {
        if (result >= 0) return;
        var buffer = stackalloc byte[256]; ffmpeg.av_strerror(result, buffer, 256);
        throw new InvalidDataException(System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)buffer));
    }
}
