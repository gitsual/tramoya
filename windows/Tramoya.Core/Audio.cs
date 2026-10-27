using System.Text;

namespace Tramoya.Core;

/// <summary>A quiet seamless stereo bed, synthesized from quantized sine partials, and its mix filters.</summary>
public static class Audio
{
    public const int SampleRate = 24000;
    public const double LoopSeconds = 32.0, TargetPeak = .10, PeakCeiling = .12;
    public static readonly double[] Partials = [1, .36, .14, .05];
    public static readonly IReadOnlyDictionary<string, double> Notes = new Dictionary<string, double>
    { ["F2"] = 87.31, ["A2"] = 110, ["C3"] = 130.81, ["D3"] = 146.83, ["E3"] = 164.81, ["F3"] = 174.61, ["G3"] = 196, ["A3"] = 220, ["B3"] = 246.94, ["C4"] = 261.63 };
    public static readonly string[][] Chords = [["A2", "C3", "E3", "G3", "A3"], ["C3", "E3", "G3", "B3"], ["F2", "C3", "E3", "A3"], ["D3", "F3", "A3", "C3"]];
    public static double Quantize(double hz) => Math.Round(hz * LoopSeconds) / LoopSeconds;
    private static double[] NoteWave(double hz, double phase, int frames)
    {
        var wave = new double[frames];
        for (int k = 1; k <= Partials.Length; k++)
        {
            double step = 2 * Math.PI * Quantize(hz * k) / SampleRate, amp = Partials[k - 1];
            for (int i = 0; i < frames; i++) wave[i] += amp * Math.Sin(step * i + phase);
        }
        return wave;
    }
    public static List<(double Left, double Right)> BuildLoop()
    {
        int frames = (int)(LoopSeconds * SampleRate);
        var names = Chords.SelectMany(c => c).Distinct().Order(StringComparer.Ordinal).ToArray();
        var left = names.Select(n => NoteWave(Notes[n], 0, frames)).ToArray();
        var right = names.Select((n, i) => NoteWave(Notes[n], Math.PI * 2 * (i * .618 % 1.0), frames)).ToArray();
        var chords = Chords.Select(c => c.Select(n => Array.IndexOf(names, n)).ToArray()).ToArray();
        double span = LoopSeconds / chords.Length, peak = 0;
        var raw = new List<(double Left, double Right)>(frames);
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / SampleRate, l = 0, r = 0;
            for (int c = 0; c < chords.Length; c++)
            {
                double centre = c * span + span / 2;
                double window = Math.Cos(2 * Math.PI * (t - centre) / LoopSeconds);
                if (window <= 0) continue;
                double gain = window * window / chords[c].Length;
                foreach (int n in chords[c]) { l += gain * left[n][i]; r += gain * right[n][i]; }
            }
            raw.Add((l, r)); peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
        }
        double scale = peak != 0 ? TargetPeak / peak : 0;
        for (int i = 0; i < frames; i++) raw[i] = (raw[i].Left * scale, raw[i].Right * scale);
        return raw;
    }
    public static string RenderWav(IEnumerable<(double Left, double Right)> frames, string path)
    {
        var samples = frames as IReadOnlyCollection<(double Left, double Right)> ?? frames.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new BinaryWriter(File.Create(path));
        void Tag(string s) => writer.Write(Encoding.ASCII.GetBytes(s));
        int bytes = checked(samples.Count * 4);
        Tag("RIFF"); writer.Write(36 + bytes); Tag("WAVE"); Tag("fmt "); writer.Write(16);
        writer.Write((short)1); writer.Write((short)2); writer.Write(SampleRate); writer.Write(SampleRate * 4);
        writer.Write((short)4); writer.Write((short)16); Tag("data"); writer.Write(bytes);
        foreach (var (l, r) in samples) { writer.Write(checked((short)(int)(l * 32767))); writer.Write(checked((short)(int)(r * 32767))); }
        return path;
    }
    public static string DuckFilter(double voiceVolume = 3.2, double threshold = .02, int ratio = 8, int attack = 20, int release = 800) =>
        "[0:a]asplit=2[voz][key];" + $"[1:a]aresample=48000,volume={Fmt.Py(voiceVolume)}[bed];" +
        FormattableString.Invariant($"[bed][key]sidechaincompress=threshold={Fmt.Py(threshold)}:ratio={ratio}:attack={attack}:release={release}[ducked];") +
        "[voz][ducked]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[out]";
    public static string SoloFilter(double volume = 1.6) => $"[1:a]aresample=48000,volume={Fmt.Py(volume)}[out]";
    public static string[] MixArgv(string video, string bedWav, string outPath, bool duck) =>
        ["ffmpeg", "-y", "-i", video, "-stream_loop", "-1", "-i", bedWav, "-filter_complex", duck ? DuckFilter() : SoloFilter(), "-map", "0:v", "-map", "[out]", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-shortest", outPath];
}
