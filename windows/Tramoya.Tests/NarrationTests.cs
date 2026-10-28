using System.Text;
using Tramoya.Core;

namespace Tramoya.Tests;

/// <summary>Port of <c>tests/test_narration.py</c>. Every test gets its own temp directory, like pytest's <c>tmp_path</c>.</summary>
public class NarrationTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tramoya-tests-" + Guid.NewGuid().ToString("N"));

    public NarrationTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    /// <summary>What Python's <c>wave</c> module writes for 16-bit mono silence.</summary>
    internal static void WriteSilentWav(string path, double seconds = 0.2, int framerate = 16000)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var frames = (int)(seconds * framerate);
        var dataSize = frames * 2;
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataSize);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // channels
        w.Write(framerate);
        w.Write(framerate * 2);     // byte rate
        w.Write((short)2);          // block align
        w.Write((short)16);         // bits per sample
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize);
        w.Write(new byte[dataSize]);
    }

    private static Pacing Silent(bool rehearse = false, string lang = "en") =>
        new(rehearse: rehearse, lang: lang, sleep: _ => Task.CompletedTask);

    // --- Script ------------------------------------------------------------

    [Fact]
    public void ScriptTextReturnsTextForKeyAndLang()
    {
        var script = new Script(new Dictionary<string, Dictionary<string, string>>
        {
            ["intro"] = new() { ["es"] = "Hola", ["en"] = "Hello" },
        });
        Assert.Equal("Hola", script.Text("intro", "es"));
        Assert.Equal("Hello", script.Text("intro", "en"));
    }

    [Fact]
    public void ScriptKeys()
    {
        var script = new Script(new Dictionary<string, Dictionary<string, string>>
        {
            ["intro"] = new() { ["en"] = "Hello" },
            ["outro"] = new() { ["en"] = "Bye" },
        });
        Assert.Equal(new HashSet<string> { "intro", "outro" }, script.Keys.ToHashSet());
    }

    [Fact]
    public void ScriptSupportsLenAndContains()
    {
        var script = new Script(new Dictionary<string, Dictionary<string, string>>
        {
            ["intro"] = new() { ["en"] = "Hello" },
        });
        Assert.Equal(1, script.Count);
        Assert.True(script.ContainsKey("intro"));
        Assert.False(script.ContainsKey("missing"));
    }

    // --- DeckScript ----------------------------------------------------------

    [Fact]
    public void DeckScriptPagesAndText()
    {
        var deck = new DeckScript(new Dictionary<string, List<(string Page, string Text)>>
        {
            ["en"] = new() { ("cover", "Cover text"), ("agenda", "Agenda text") },
        });
        Assert.Equal(new[] { "cover", "agenda" }, deck.Pages("en"));
        Assert.Equal("Cover text", deck.Text("cover", "en"));
        Assert.Equal("Agenda text", deck.Text("agenda", "en"));
    }

    // --- WavSeconds -----------------------------------------------------------

    [Fact]
    public void WavSecondsReadsDuration()
    {
        var path = Path.Combine(_tmp, "clip.wav");
        WriteSilentWav(path, seconds: 0.5, framerate: 16000);
        Assert.Equal(0.5, Narration.WavSeconds(path), 0.01);
    }

    [Fact]
    public void WavSecondsReturnsZeroForMissingFile()
    {
        Assert.Equal(0.0, Narration.WavSeconds(Path.Combine(_tmp, "missing.wav")));
    }

    [Fact]
    public void WavSecondsReturnsZeroForCorruptFile()
    {
        var path = Path.Combine(_tmp, "bad.wav");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("not a wav file"));
        Assert.Equal(0.0, Narration.WavSeconds(path));
    }

    // --- Narrator ----------------------------------------------------------

    private sealed class FakePlayer
    {
        public List<string> Calls { get; } = new();
        public List<string> Waited { get; } = new();

        public IClipPlayback Play(string path)
        {
            Calls.Add(path);
            return new FakeProc(path, Waited);
        }
    }

    private sealed class FakeProc(string path, List<string> waited) : IClipPlayback
    {
        public Task WaitAsync()
        {
            waited.Add(path);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task NarratorPlaysClipAndWaitsForDuration()
    {
        var clip = Path.Combine(_tmp, "en", "intro.wav");
        WriteSilentWav(clip);
        var player = new FakePlayer();
        var narrator = new Narrator(_tmp, Silent(), player.Play);

        var duration = await narrator.SayAsync("intro", fallback: 5.0, wait: true);

        Assert.Equal(new[] { clip }, player.Calls);
        Assert.Equal(new[] { clip }, player.Waited);
        Assert.Equal(0.2, duration, 0.05);
    }

    [Fact]
    public async Task NarratorFallsBackToPacingSleepWhenNoClip()
    {
        var slept = new List<double>();
        var pacing = new Pacing(sleep: s => { slept.Add(s); return Task.CompletedTask; });
        var player = new FakePlayer();
        var narrator = new Narrator(_tmp, pacing, player.Play);

        await narrator.SayAsync("missing-key", fallback: 3.0, wait: true);

        Assert.Empty(player.Calls);
        Assert.Equal(new[] { 3.0 }, slept);
    }

    [Fact]
    public async Task NarratorNoWaitDoesNotBlockImmediately()
    {
        var clip = Path.Combine(_tmp, "en", "intro.wav");
        WriteSilentWav(clip);
        var player = new FakePlayer();
        var narrator = new Narrator(_tmp, Silent(), player.Play);

        await narrator.SayAsync("intro", wait: false);

        Assert.Equal(new[] { clip }, player.Calls);
        Assert.Empty(player.Waited);
    }

    [Fact]
    public async Task NarratorRehearsalSkipsWaitEvenWhenWaitTrue()
    {
        var clip = Path.Combine(_tmp, "en", "intro.wav");
        WriteSilentWav(clip);
        var player = new FakePlayer();
        var narrator = new Narrator(_tmp, Silent(rehearse: true), player.Play);

        await narrator.SayAsync("intro", wait: true);

        Assert.Equal(new[] { clip }, player.Calls);
        Assert.Empty(player.Waited);
    }

    [Fact]
    public async Task NarratorWaitPendingBlocksOnPreviousNoWaitClip()
    {
        var clip = Path.Combine(_tmp, "en", "intro.wav");
        WriteSilentWav(clip);
        var player = new FakePlayer();
        var narrator = new Narrator(_tmp, Silent(), player.Play);

        await narrator.SayAsync("intro", wait: false);
        Assert.Empty(player.Waited);
        await narrator.WaitPendingAsync();
        Assert.Equal(new[] { clip }, player.Waited);
    }

    [Fact]
    public void NarratorClipLooksUpLangSubdir()
    {
        var clipEn = Path.Combine(_tmp, "en", "hello.wav");
        WriteSilentWav(clipEn);
        var narrator = new Narrator(_tmp, Silent(lang: "en"));
        Assert.Equal(clipEn, narrator.Clip("hello"));

        var narratorEs = new Narrator(_tmp, Silent(lang: "es"));
        Assert.Null(narratorEs.Clip("hello"));
    }
}
