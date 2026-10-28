using Tramoya.Core;
using Xunit;

namespace Tramoya.Tests;

public class AudioTests
{
    private static readonly Lazy<List<(double Left, double Right)>> Loop = new(Audio.BuildLoop);
    private static double Peak() => Loop.Value.Max(f => Math.Max(Math.Abs(f.Left), Math.Abs(f.Right)));
    private static string Temp() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
    [Fact] public void EveryPartialCompletesWholeCyclesInTheLoop()
    {
        foreach (double hz in new[] { 55, 110, 220, 329.63, 440 }) { double fitted = Audio.Quantize(hz), cycles = fitted * Audio.LoopSeconds; Assert.Equal(Math.Round(cycles), cycles, 9); Assert.True(Math.Abs(fitted - hz) < .5); }
    }
    [Fact] public void TheLoopWrapsWithoutAClick()
    {
        var loop = Loop.Value; double inner = 0;
        for (int i = 1; i < loop.Count; i++) inner = Math.Max(inner, Math.Max(Math.Abs(loop[i].Left - loop[i - 1].Left), Math.Abs(loop[i].Right - loop[i - 1].Right)));
        double wrap = Math.Max(Math.Abs(loop[0].Left - loop[^1].Left), Math.Abs(loop[0].Right - loop[^1].Right)); Assert.True(wrap <= inner);
    }
    [Fact] public void TheBedNeverClips() => Assert.True(Peak() < 1);
    [Fact] public void TheBedSitsFarBelowSpeech() => Assert.True(Peak() <= Audio.PeakCeiling);
    [Fact] public void ItIsDeterministic() => Assert.Equal(Loop.Value, Audio.BuildLoop());
    [Fact] public void TheChordsShareNotesSoNothingLurches()
    {
        for (int i = 0; i < Audio.Chords.Length; i++) Assert.True(Audio.Chords[i].Intersect(Audio.Chords[(i + 1) % Audio.Chords.Length]).Count() >= 2);
    }
    [Fact] public void RenderWavRoundTripsTheFrames()
    {
        string path = Temp(); try { Audio.RenderWav([(.5, -.5), (0, .25)], path); using var reader = new BinaryReader(File.OpenRead(path)); reader.BaseStream.Position = 44; Assert.Equal(new short[] { 16383, -16383, 0, 8191 }, Enumerable.Range(0, 4).Select(_ => reader.ReadInt16()).ToArray()); } finally { File.Delete(path); }
    }
    [Fact] public void RenderWavWritesTheShippedFormat()
    {
        string path = Temp(); try { Audio.RenderWav(Loop.Value, path); using var r = new BinaryReader(File.OpenRead(path)); Assert.Equal("RIFF", new string(r.ReadChars(4))); Assert.Equal(36 + 768000 * 4, r.ReadInt32()); Assert.Equal("WAVEfmt ", new string(r.ReadChars(8))); Assert.Equal(16, r.ReadInt32()); Assert.Equal(1, r.ReadInt16()); Assert.Equal(2, r.ReadInt16()); Assert.Equal(Audio.SampleRate, r.ReadInt32()); Assert.Equal(Audio.SampleRate * 4, r.ReadInt32()); Assert.Equal(4, r.ReadInt16()); Assert.Equal(16, r.ReadInt16()); Assert.Equal("data", new string(r.ReadChars(4))); Assert.Equal((int)(Audio.LoopSeconds * Audio.SampleRate), r.ReadInt32() / 4); } finally { File.Delete(path); }
    }
    [Fact] public void DuckFilterMatchesTheRecordedDryRun() => Assert.Equal("[0:a]asplit=2[voz][key];[1:a]aresample=48000,volume=3.2[bed];[bed][key]sidechaincompress=threshold=0.02:ratio=8:attack=20:release=800[ducked];[voz][ducked]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[out]", Audio.DuckFilter());
    [Fact] public void SoloFilterMatchesTheRecordedDryRun() => Assert.Equal("[1:a]aresample=48000,volume=1.6[out]", Audio.SoloFilter());
    [Fact] public void MixArgvDuckMode() => Assert.Equal(new[] { "ffmpeg", "-y", "-i", "demo-captions.mp4", "-stream_loop", "-1", "-i", "bed-32s.wav", "-filter_complex", Audio.DuckFilter(), "-map", "0:v", "-map", "[out]", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-shortest", "demo-musica.mp4" }, Audio.MixArgv("demo-captions.mp4", "bed-32s.wav", "demo-musica.mp4", true));
    [Fact] public void MixArgvSoloModeDropsTheSourceAudio()
    {
        var args = Audio.MixArgv("demo-mudo.mp4", "bed-32s.wav", "demo-directo.mp4", false); Assert.Contains("-filter_complex", args); Assert.Equal(Audio.SoloFilter(), args[Array.IndexOf(args, "-filter_complex") + 1]); Assert.DoesNotContain("0:a", string.Join(" ", args).Replace("0:v", "")); Assert.Equal("demo-directo.mp4", args[^1]);
    }
}
