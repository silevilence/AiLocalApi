using System.Runtime.InteropServices;
using System.Text;
using AiLocalApi.Contracts;
using Whisper.net;

namespace AiLocalApi.Worker;

// Segment actual Unicode words with Windows ICU, then aggregate the native aligned
// token spans that overlap each word. Token UTF-8 bytes come from the verified GGML
// vocabulary: decoding each token separately loses split Chinese UTF-8 characters.
internal sealed class WordAlignment
{
    private readonly byte[][] vocabulary;
    public WordAlignment(string model)
    {
        using var reader = new BinaryReader(File.OpenRead(model));
        if (reader.ReadUInt32() != 0x67676d6c) throw new InvalidDataException("Expected GGML model.");
        reader.BaseStream.Position = 48;
        var mel = reader.ReadInt32(); var fft = reader.ReadInt32();
        if (mel is < 1 or > 256 || fft is < 1 or > 1000) throw new InvalidDataException("Invalid GGML filters.");
        reader.BaseStream.Position += mel * fft * 4L;
        var count = reader.ReadInt32(); if (count is < 1 or > 100000) throw new InvalidDataException("Invalid vocabulary.");
        vocabulary = new byte[count][];
        for (var i = 0; i < count; i++) { var size = reader.ReadInt32(); if (size is < 0 or > 4096) throw new InvalidDataException("Invalid token."); vocabulary[i] = reader.ReadBytes(size); }
    }
    public Word[] Align(SegmentData segment, double offset, double duration)
    {
        using var bytes = new MemoryStream();
        var tokens = new List<(int Start, int End, long TimeStart, long TimeEnd)>();
        foreach (var token in segment.Tokens)
        {
            if (token.Id < 0 || token.Id >= vocabulary.Length) continue;
            var value = vocabulary[token.Id];
            if (value.AsSpan().StartsWith("<|"u8)) continue;
            var start = (int)bytes.Length; bytes.Write(value);
            tokens.Add((start, (int)bytes.Length, token.Start, token.End));
        }
        var text = new UTF8Encoding(false, true).GetString(bytes.ToArray());
        if (text.Trim() != segment.Text.Trim()) throw new InvalidDataException("Cannot align token bytes to segment text.");
        var memory = Marshal.StringToHGlobalUni(text); IntPtr iterator = IntPtr.Zero;
        try
        {
            var error = 0; iterator = ubrk_open(1, segment.Language, memory, text.Length, ref error);
            if (error > 0 || iterator == IntPtr.Zero) throw new InvalidOperationException("ICU word segmentation unavailable.");
            var result = new List<Word>(); var start = ubrk_first(iterator); int end;
            while ((end = ubrk_next(iterator)) != -1)
            {
                if (ubrk_getRuleStatus(iterator) >= 100)
                {
                    var from = Encoding.UTF8.GetByteCount(text.AsSpan(0, start)); var to = from + Encoding.UTF8.GetByteCount(text.AsSpan(start, end - start));
                    var matches = tokens.Where(token => token.Start < to && token.End > from).ToArray();
                    if (matches.Length == 0 || matches.Any(token => token.TimeStart < 0 || token.TimeEnd < token.TimeStart)) throw new InvalidDataException("Native token alignment unavailable for this word.");
                    var first = Math.Clamp(offset + matches.Min(token => token.TimeStart) / 100d, 0, duration);
                    var last = Math.Clamp(offset + matches.Max(token => token.TimeEnd) / 100d, first, duration);
                    result.Add(new(text[start..end], first, last));
                }
                start = end;
            }
            return result.ToArray();
        }
        finally { if (iterator != IntPtr.Zero) ubrk_close(iterator); Marshal.FreeHGlobal(memory); }
    }
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr ubrk_open(int type, [MarshalAs(UnmanagedType.LPStr)] string locale, IntPtr text, int length, ref int error);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int ubrk_first(IntPtr iterator);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int ubrk_next(IntPtr iterator);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int ubrk_getRuleStatus(IntPtr iterator);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void ubrk_close(IntPtr iterator);
}
