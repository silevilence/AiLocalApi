using System.Numerics;

namespace AiLocalApi.Worker;

// Whisper's 16 kHz, 400-sample periodic Hann STFT and Slaney-normalized mel bank.
internal sealed class WhisperFeatures
{
    private const int N = 400, Bins = 201, Frames = 3000;
    private static readonly float[][] Cosines = Basis(false), Sines = Basis(true);
    private readonly float[][] filters;
    public WhisperFeatures(int melCount)
    {
        if (melCount is not (80 or 128)) throw new InvalidDataException("Unsupported Whisper mel count.");
        static double Mel(double hz) => hz < 1000 ? hz / (200d / 3) : 15 + Math.Log(hz / 1000) / (Math.Log(6.4) / 27);
        static double Hertz(double mel) => mel < 15 ? mel * (200d / 3) : 1000 * Math.Exp((mel - 15) * (Math.Log(6.4) / 27));
        var edges = Enumerable.Range(0, melCount + 2).Select(i => Hertz(Mel(8000) * i / (melCount + 1))).ToArray();
        filters = Enumerable.Range(0, melCount).Select(m => Enumerable.Range(0, Bins).Select(k =>
            (float)(Math.Max(0, Math.Min((k * 40 - edges[m]) / (edges[m + 1] - edges[m]),
                (edges[m + 2] - k * 40) / (edges[m + 2] - edges[m + 1]))) * 2 / (edges[m + 2] - edges[m]))).ToArray()).ToArray();
    }
    private static float[][] Basis(bool imaginary) => Enumerable.Range(0, Bins).Select(k => Enumerable.Range(0, N)
        .Select(n => (float)((0.5 - 0.5 * Math.Cos(2 * Math.PI * n / N)) *
            (imaginary ? Math.Sin(2 * Math.PI * k * n / N) : Math.Cos(2 * Math.PI * k * n / N)))).ToArray()).ToArray();
    private static float Dot(float[] left, float[] right)
    {
        var sum = Vector<float>.Zero; var i = 0;
        for (; i <= left.Length - Vector<float>.Count; i += Vector<float>.Count) sum += new Vector<float>(left, i) * new Vector<float>(right, i);
        float result = Vector.Sum(sum);
        for (; i < left.Length; i++) result += left[i] * right[i];
        return result;
    }
    public float[] Extract(float[] samples, CancellationToken token)
    {
        if (samples.Length > 480000) throw new ArgumentException("Whisper window exceeds 30 seconds.");
        var output = new float[filters.Length * Frames];
        Parallel.For(0, Frames, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) }, frame =>
        {
            var window = new float[N]; var power = new float[Bins];
            for (var i = 0; i < N; i++)
            {
                var index = frame * 160 + i - N / 2;
                if (index < 0) index = -index;
                if (index >= 480000) index = 960000 - index - 2;
                window[i] = index < samples.Length ? samples[index] : 0;
            }
            for (var k = 0; k < Bins; k++) { var real = Dot(window, Cosines[k]); var imaginary = Dot(window, Sines[k]); power[k] = real * real + imaginary * imaginary; }
            for (var m = 0; m < filters.Length; m++) output[m * Frames + frame] = MathF.Log10(MathF.Max(1e-10f, Dot(power, filters[m])));
        });
        var floor = output.Max() - 8;
        for (var i = 0; i < output.Length; i++) output[i] = (MathF.Max(output[i], floor) + 4) / 4;
        return output;
    }
}
