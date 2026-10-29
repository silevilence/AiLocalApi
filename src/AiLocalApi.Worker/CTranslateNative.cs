using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AiLocalApi.Worker;

internal sealed class CTranslateNative : SafeHandleZeroOrMinusOneIsInvalid
{
    public int MelCount { get; }
    public bool Multilingual { get; }
    public string ComputeType { get; }
    public CTranslateNative(string path, string backend, string precision) : base(true)
    {
        if (ct2_abi() != 1) throw new InvalidOperationException("CTranslate2 bridge ABI mismatch.");
        Check(ct2_create(path, backend, precision, Math.Clamp(Environment.ProcessorCount - 1, 1, 8), out var pointer));
        SetHandle(pointer);
        try
        {
            var effective = new StringBuilder(64);
            Check(ct2_info(this, out var mels, out var multilingual, effective, effective.Capacity));
            MelCount = mels; Multilingual = multilingual != 0; ComputeType = effective.ToString();
        }
        catch { Dispose(); throw; }
    }
    public void Encode(float[] features) => Check(ct2_encode(this, features, MelCount, 3000));
    public string DetectLanguage()
    {
        if (!Multilingual) return "en";
        var result = new StringBuilder(64); Check(ct2_detect(this, result, result.Capacity));
        return result.ToString().Replace("<|", "").Replace("|>", "");
    }
    public (int[] Tokens, float Score, float NoSpeech) Generate(int[] prompt, float temperature)
    {
        var output = new int[448]; Check(ct2_generate(this, prompt, prompt.Length, temperature, output, output.Length, out var count, out var score, out var noSpeech));
        return (output[..count], score, noSpeech);
    }
    public (int Text, int Time)[] Align(int[] start, int[] text, int frames)
    {
        var pairs = new int[8192]; Check(ct2_align(this, start, start.Length, text, text.Length, frames, pairs, pairs.Length, out var count));
        return Enumerable.Range(0, count).Select(i => (pairs[i * 2], pairs[i * 2 + 1])).ToArray();
    }
    private static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException("CTranslate2: " + Marshal.PtrToStringUTF8(ct2_error()));
    }
    protected override bool ReleaseHandle() { ct2_destroy(handle); return true; }
    private const string Library = "ailocal_ct2";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int ct2_abi();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr ct2_error();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int ct2_create([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string backend, [MarshalAs(UnmanagedType.LPUTF8Str)] string precision, int threads, out IntPtr output);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void ct2_destroy(IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int ct2_info(CTranslateNative handle, out int mels, out int multilingual, StringBuilder precision, int capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int ct2_encode(CTranslateNative handle, float[] features, int mels, int frames);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int ct2_detect(CTranslateNative handle, StringBuilder language, int capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int ct2_generate(CTranslateNative handle, int[] prompt, int promptCount, float temperature, [Out] int[] tokens, int capacity, out int count, out float score, out float noSpeech);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int ct2_align(CTranslateNative handle, int[] start, int startCount, int[] text, int textCount, int frames, [Out] int[] pairs, int capacity, out int count);
}
