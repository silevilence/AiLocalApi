using FFmpeg.AutoGen;
namespace AiLocalApi.Verify;

// Test-fixture encoder uses shared FFmpeg libraries only. Input is the canonical
// mono PCM16/16 kHz JFK fixture. Production decoding lives in the Worker.
internal static unsafe class AudioFixtures
{
    public static void Generate(string wave, string nativeDirectory)
    {
        ffmpeg.RootPath = Path.GetFullPath(nativeDirectory); ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        using var reader = new BinaryReader(File.OpenRead(wave));
        reader.BaseStream.Position = 12; byte[]? pcm = null;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var tag = new string(reader.ReadChars(4)); var length = reader.ReadInt32();
            if (tag == "data") { pcm = reader.ReadBytes(length); break; } reader.BaseStream.Position += length + (length & 1);
        }
        if (pcm is null) throw new InvalidDataException("Fixture WAV has no data chunk.");
        var samples = new float[pcm.Length / 2]; for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
        using (var output = new BinaryWriter(File.Create(Path.Combine(Path.GetDirectoryName(wave)!, "long.wav"))))
        {
            output.Write("RIFF"u8); output.Write(36 + pcm.Length * 4); output.Write("WAVEfmt "u8); output.Write(16); output.Write((short)1); output.Write((short)1); output.Write(16000); output.Write(32000); output.Write((short)2); output.Write((short)16); output.Write("data"u8); output.Write(pcm.Length * 4); for (var i = 0; i < 4; i++) output.Write(pcm);
        }
        foreach (var (extension, codec) in new[] { ("mp3", "libmp3lame"), ("m4a", "aac"), ("flac", "flac"), ("ogg", "libvorbis"), ("webm", "libopus"), ("mp4", "aac") })
        {
            Encode(Path.ChangeExtension(wave, extension), codec, samples); Console.WriteLine("Created native fixture: " + extension);
        }
    }
    private static void Encode(string path, string name, float[] samples)
    {
        AVFormatContext* format = null; AVCodecContext* context = null; AVFrame* frame = null; AVPacket* packet = null;
        try
        {
            Check(ffmpeg.avformat_alloc_output_context2(&format, null, null, path));
            var codec = ffmpeg.avcodec_find_encoder_by_name(name); if (codec == null) throw new Exception("Missing encoder: " + name);
            context = ffmpeg.avcodec_alloc_context3(codec); context->sample_rate = 48000; context->time_base = new() { num = 1, den = 48000 }; context->bit_rate = 96000;
            context->sample_fmt = name == "flac" ? AVSampleFormat.AV_SAMPLE_FMT_S16 : name == "libopus" ? AVSampleFormat.AV_SAMPLE_FMT_FLT : AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            ffmpeg.av_channel_layout_default(&context->ch_layout, 1);
            if ((format->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0) context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            Check(ffmpeg.avcodec_open2(context, codec, null));
            var stream = ffmpeg.avformat_new_stream(format, null); stream->time_base = context->time_base;
            Check(ffmpeg.avcodec_parameters_from_context(stream->codecpar, context));
            Check(ffmpeg.avio_open(&format->pb, path, ffmpeg.AVIO_FLAG_WRITE)); Check(ffmpeg.avformat_write_header(format, null));
            frame = ffmpeg.av_frame_alloc(); packet = ffmpeg.av_packet_alloc();
            var frameSize = context->frame_size > 0 ? context->frame_size : 4096;
            frame->nb_samples = frameSize; frame->format = (int)context->sample_fmt; frame->sample_rate = context->sample_rate;
            Check(ffmpeg.av_channel_layout_copy(&frame->ch_layout, &context->ch_layout)); Check(ffmpeg.av_frame_get_buffer(frame, 0));
            for (var offset = 0; offset < samples.Length * 3; offset += frameSize)
            {
                Check(ffmpeg.av_frame_make_writable(frame)); frame->pts = offset;
                for (var i = 0; i < frameSize; i++)
                {
                    var index = (offset + i) / 3; var value = index < samples.Length ? samples[index] : 0;
                    if (context->sample_fmt == AVSampleFormat.AV_SAMPLE_FMT_S16) ((short*)frame->data[0])[i] = (short)Math.Clamp(value * 32768, short.MinValue, short.MaxValue);
                    else ((float*)frame->data[0])[i] = value;
                }
                Check(ffmpeg.avcodec_send_frame(context, frame)); Drain(context, packet, format, stream);
            }
            Check(ffmpeg.avcodec_send_frame(context, null)); Drain(context, packet, format, stream); Check(ffmpeg.av_write_trailer(format));
        }
        finally
        {
            ffmpeg.av_frame_free(&frame); ffmpeg.av_packet_free(&packet); ffmpeg.avcodec_free_context(&context);
            if (format != null) { ffmpeg.avio_closep(&format->pb); ffmpeg.avformat_free_context(format); }
        }
    }
    private static void Drain(AVCodecContext* context, AVPacket* packet, AVFormatContext* format, AVStream* stream)
    {
        int result;
        while ((result = ffmpeg.avcodec_receive_packet(context, packet)) >= 0)
        {
            ffmpeg.av_packet_rescale_ts(packet, context->time_base, stream->time_base); packet->stream_index = stream->index;
            Check(ffmpeg.av_interleaved_write_frame(format, packet)); ffmpeg.av_packet_unref(packet);
        }
        if (result != ffmpeg.AVERROR_EOF && result != ffmpeg.AVERROR(ffmpeg.EAGAIN)) Check(result);
    }
    private static void Check(int code) { if (code < 0) throw new Exception("FFmpeg fixture error: " + code); }
}
