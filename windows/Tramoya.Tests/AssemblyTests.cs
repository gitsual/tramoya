using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Tramoya.Core;

namespace Tramoya.Tests;

/// <summary>
/// Port of <c>tests/test_assembly.py</c> (plus the one real encode from
/// <c>tests/test_slow_pipeline.py</c>). Everything here drives a <c>Runner(dryRun: true)</c>:
/// no real ffmpeg process ever starts, and every assertion reads back <c>runner.Calls</c>
/// or the returned plan. Every test gets its own temp directory, like pytest's <c>tmp_path</c>.
/// </summary>
public class AssemblyTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tramoya-tests-" + Guid.NewGuid().ToString("N"));

    public AssemblyTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    private string T(params string[] parts) => Path.Combine(new[] { _tmp }.Concat(parts).ToArray());

    private static string After(string[] argv, string flag) => argv[Array.IndexOf(argv, flag) + 1];

    // --------------------------------------------------------------------------
    // Segment / PlanSegments
    // --------------------------------------------------------------------------

    [Fact]
    public void SegmentIsAFrozenValueObject()
    {
        var seg = new Segment("slide", "cover", "01.png", 4.0);
        Assert.Null(seg.Voice);
        // Python raises on assignment; here the compiler refuses it, which shows up as
        // init-only setters on every positional property.
        var setter = typeof(Segment).GetProperty(nameof(Segment.Duration))!.SetMethod!;
        Assert.Contains(typeof(IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
        Assert.Equal(seg, new Segment("slide", "cover", "01.png", 4.0));
    }

    [Fact]
    public void TheRecordingIsPlayedOnThePageThatAnnouncesIt()
    {
        var plan = Core.Assembly.PlanSegments(
            pages: new Dictionary<string, string> { ["portada"] = "01", ["video"] = "02", ["cierre"] = "03" },
            order: new[] { "portada", "video", "cierre" },
            durations: new Dictionary<string, double> { ["portada"] = 4.0, ["video"] = 3.0, ["cierre"] = 5.0 },
            slidesDir: _tmp,
            clip: T("demo.mp4"),
            clipDuration: 145.0);
        Assert.Equal(new[] { "slide", "slide", "clip", "slide" }, plan.Select(s => s.Kind));
        Assert.Equal(T("demo.mp4"), plan[2].Source);
        Assert.Equal(145.0, plan[2].Duration, 6);
    }

    [Fact]
    public void EverySlideSegmentLastsAsLongAsItsNarration()
    {
        var plan = Core.Assembly.PlanSegments(
            pages: new Dictionary<string, string> { ["portada"] = "01", ["cierre"] = "02" },
            order: new[] { "portada", "cierre" },
            durations: new Dictionary<string, double> { ["portada"] = 4.5, ["cierre"] = 9.25 },
            slidesDir: _tmp,
            clip: null,
            clipDuration: 0.0);
        Assert.Equal(4.5 + Core.Assembly.PageTail, plan[0].Duration, 6);
        Assert.Equal(9.25 + Core.Assembly.PageTail, plan[1].Duration, 6);
        Assert.Equal(new[] { "01.png", "02.png" }, plan.Select(s => Path.GetFileName(s.Source)));
    }

    [Fact]
    public void APageWithoutAVoiceClipStillGetsTimeOnScreen()
    {
        var plan = Core.Assembly.PlanSegments(
            pages: new Dictionary<string, string> { ["seccion_1"] = "01" },
            order: new[] { "seccion_1" },
            durations: new Dictionary<string, double>(),
            slidesDir: _tmp,
            clip: null,
            clipDuration: 0.0);
        Assert.Equal(Core.Assembly.SilentPage, plan[0].Duration, 6);
        Assert.Null(plan[0].Voice);
    }

    [Fact]
    public void ClipPageIsConfigurable()
    {
        var plan = Core.Assembly.PlanSegments(
            pages: new Dictionary<string, string> { ["intro"] = "01", ["showcase"] = "02" },
            order: new[] { "intro", "showcase" },
            durations: new Dictionary<string, double> { ["intro"] = 2.0, ["showcase"] = 2.0 },
            slidesDir: _tmp,
            clip: T("demo.mp4"),
            clipDuration: 10.0,
            clipPage: "showcase");
        Assert.Equal(new[] { "slide", "slide", "clip" }, plan.Select(s => s.Kind));
    }

    // --------------------------------------------------------------------------
    // HumanizeWait
    // --------------------------------------------------------------------------

    [Fact]
    public void HumanizeWaitInSpanish()
    {
        Assert.Equal("unos segundos despues", Core.Assembly.HumanizeWait(3.0, "es"));
        Assert.Equal("1 minuto despues", Core.Assembly.HumanizeWait(60.0, "es"));
        Assert.Equal("4 minutos despues", Core.Assembly.HumanizeWait(240.0, "es"));
    }

    [Fact]
    public void HumanizeWaitInEnglish()
    {
        Assert.Equal("a few seconds later", Core.Assembly.HumanizeWait(3.0, "en"));
        Assert.Equal("1 minute later", Core.Assembly.HumanizeWait(60.0, "en"));
        Assert.Equal("4 minutes later", Core.Assembly.HumanizeWait(240.0, "en"));
    }

    // --------------------------------------------------------------------------
    // BuildSceneClip
    // --------------------------------------------------------------------------

    private static readonly (double, double)[] NoWaits = Array.Empty<(double, double)>();

    [Fact]
    public void BuildSceneClipCutsAPlainWindowWithNoWaits()
    {
        var runner = new Runner(dryRun: true);
        var outPath = Core.Assembly.BuildSceneClip(runner, "in.mp4", _tmp, "intro", 10.0, 20.0, NoWaits, "en");
        Assert.Single(runner.Calls);
        var argv = runner.Calls[0];
        Assert.Equal("ffmpeg", argv[0]);
        Assert.Contains("-ss", argv);
        Assert.Equal("10.0", After(argv, "-ss"));
        Assert.Contains("-to", argv);
        Assert.Equal("20.0", After(argv, "-to"));
        Assert.Contains("-an", argv);
        Assert.Equal(T("scene-intro-video.mp4"), outPath);
    }

    [Fact]
    public void BuildSceneClipClampsANearZeroScene()
    {
        var runner = new Runner(dryRun: true);
        Core.Assembly.BuildSceneClip(runner, "in.mp4", _tmp, "s", 5.0, 5.0, NoWaits, "en");
        var argv = runner.Calls[0];
        Assert.Equal(Fmt.Py(5.0 + Core.Assembly.MinScene), After(argv, "-to"));
    }

    [Fact]
    public void BuildSceneClipIgnoresWaitsAtOrUnderTheThreshold()
    {
        var runner = new Runner(dryRun: true);
        Core.Assembly.BuildSceneClip(
            runner, "in.mp4", _tmp, "s", 0.0, 30.0,
            new[] { (5.0, 5.0 + Core.Assembly.WaitThreshold) }, "en");
        Assert.True(runner.Calls.Count == 1, "a wait no longer than the threshold is left alone");
    }

    [Fact]
    public void BuildSceneClipCompressesALongWaitAndConcats()
    {
        var runner = new Runner(dryRun: true);
        var outPath = Core.Assembly.BuildSceneClip(
            runner, "in.mp4", _tmp, "s", 0.0, 40.0,
            new[] { (5.0, 5.0 + Core.Assembly.WaitThreshold + 10.0) }, "en");
        // pre-wait cut, the sped-up wait itself, the post-wait cut, then concat.
        Assert.Equal(4, runner.Calls.Count);
        var (pre, fast, post, concat) = (runner.Calls[0], runner.Calls[1], runner.Calls[2], runner.Calls[3]);
        Assert.DoesNotContain("-vf", pre);
        Assert.Contains("setpts=PTS/", After(fast, "-vf"));
        Assert.Contains("drawtext=text='", After(fast, "-vf"));
        Assert.DoesNotContain("-vf", post);
        Assert.Equal(new[] { "ffmpeg", "-loglevel" }, concat.Take(2));
        Assert.Contains("concat", concat);
        Assert.Equal(T("scene-s-video.mp4"), outPath);
        Assert.Single(Directory.GetFiles(_tmp, "scene-s-concat.txt"));
    }

    [Fact]
    public void BuildSceneClipEmitsNoEmptyPostCutWhenTheWaitReachesTheEnd()
    {
        var runner = new Runner(dryRun: true);
        Core.Assembly.BuildSceneClip(
            runner, "in.mp4", _tmp, "s", 0.0, 40.0,
            new[] { (5.0, 40.0) }, "en");
        // pre-wait cut, the sped-up wait, concat: a zero-length "post" cut would
        // make ffmpeg fail on a real take.
        Assert.Equal(3, runner.Calls.Count);
        Assert.DoesNotContain(runner.Calls, c => c.Contains(T("scene-s-post.mp4")));
    }

    [Fact]
    public void BuildSceneClipSpeedsUpByTheTargetRatio()
    {
        var runner = new Runner(dryRun: true);
        var waitDur = 40.0;
        Core.Assembly.BuildSceneClip(runner, "in.mp4", _tmp, "s", 0.0, waitDur, new[] { (0.0, waitDur) }, "en");
        var fast = runner.Calls[0];
        var factor = waitDur / Core.Assembly.WaitTarget;
        Assert.Contains($"setpts=PTS/{Fmt.Py(factor)},", After(fast, "-vf"));
    }

    // --------------------------------------------------------------------------
    // FitAndMux
    // --------------------------------------------------------------------------

    [Fact]
    public void FitAndMuxPadsVideoWhenTheVoiceRunsLonger()
    {
        var runner = new Runner(dryRun: true);
        var video = T("clip.mp4");
        var wav = T("voice.wav");
        runner.FakeDurations[video] = 3.0;
        runner.FakeDurations[wav] = 5.0;
        var (outPath, finalDur) = Core.Assembly.FitAndMux(runner, _tmp, "s", video, wav);
        Assert.Equal(5.0, finalDur, 6);
        Assert.Equal(2, runner.Calls.Count);
        var (padCall, muxCall) = (runner.Calls[0], runner.Calls[1]);
        Assert.Contains("tpad=stop_mode=clone", After(padCall, "-vf"));
        Assert.Contains("-filter_complex", muxCall);
        Assert.Contains("apad=whole_dur=5.000", After(muxCall, "-filter_complex"));
        Assert.Equal(T("scene-s-final.mp4"), outPath);
    }

    [Fact]
    public void FitAndMuxSkipsPaddingWhenVideoAlreadyCoversTheVoice()
    {
        var runner = new Runner(dryRun: true);
        var video = T("clip.mp4");
        var wav = T("voice.wav");
        runner.FakeDurations[video] = 10.0;
        runner.FakeDurations[wav] = 2.0;
        Core.Assembly.FitAndMux(runner, _tmp, "s", video, wav);
        Assert.True(runner.Calls.Count == 1, "video already covers the voice; no tpad call");
    }

    [Fact]
    public void FitAndMuxUsesSilenceWhenThereIsNoVoice()
    {
        var runner = new Runner(dryRun: true);
        var video = T("clip.mp4");
        runner.FakeDurations[video] = 4.0;
        Core.Assembly.FitAndMux(runner, _tmp, "s", video, null);
        var muxCall = runner.Calls[^1];
        Assert.Contains("anullsrc=channel_layout=mono:sample_rate=48000", string.Join(" ", muxCall));
        Assert.DoesNotContain("-filter_complex", muxCall);
    }

    // --------------------------------------------------------------------------
    // AssembleMarks
    // --------------------------------------------------------------------------

    private static void WriteMarks(string path)
    {
        var rows = new JsonArray
        {
            new JsonObject { ["t"] = 0.0, ["key"] = "scene:intro:start" },
            new JsonObject { ["t"] = 10.0, ["key"] = "scene:intro:end" },
            new JsonObject { ["t"] = 10.0, ["key"] = "scene:outro:start" },
            new JsonObject { ["t"] = 15.0, ["key"] = "scene:outro:end" },
        };
        File.WriteAllText(path, Json.Dump(rows, indent: false), new System.Text.UTF8Encoding(false));
    }

    [Fact]
    public void AssembleMarksRendersEverySceneAndConcats()
    {
        var marksPath = T("marks.json");
        WriteMarks(marksPath);
        var voiceDir = T("voice");
        Directory.CreateDirectory(voiceDir);
        File.WriteAllBytes(Path.Combine(voiceDir, "scene-intro.wav"), Array.Empty<byte>());

        var runner = new Runner(dryRun: true);
        var outPath = T("out.mp4");
        var table = Core.Assembly.AssembleMarks(runner, "in.mp4", marksPath, voiceDir, outPath);

        Assert.Equal(new[] { "intro", "outro" }, table.Select(row => row.Name));
        Assert.Equal(10.0, table[0].Raw, 6); // raw duration
        var lastCall = runner.Calls[^1];
        Assert.Contains("concat", lastCall);
        Assert.Equal(outPath, lastCall[^1]);
    }

    [Fact]
    public void AssembleMarksFallsBackToTheLegacyVoiceFilename()
    {
        var marksPath = T("marks.json");
        WriteMarks(marksPath);
        var voiceDir = T("voice");
        Directory.CreateDirectory(voiceDir);
        File.WriteAllBytes(Path.Combine(voiceDir, "escena-intro.wav"), Array.Empty<byte>());

        var runner = new Runner(dryRun: true);
        Core.Assembly.AssembleMarks(runner, "in.mp4", marksPath, voiceDir, T("out.mp4"));

        var muxCalls = runner.Calls.Where(c => c.Contains("-filter_complex")).ToList();
        Assert.Contains(muxCalls, c => string.Join(" ", c).Contains("escena-intro.wav"));
    }

    [Fact]
    public void AssembleMarksRaisesWithoutAnySceneMarks()
    {
        var marksPath = T("marks.json");
        File.WriteAllText(marksPath, "[]", new System.Text.UTF8Encoding(false));
        var runner = new Runner(dryRun: true);
        Assert.Throws<InvalidOperationException>(() =>
            Core.Assembly.AssembleMarks(runner, "in.mp4", marksPath, _tmp, T("out.mp4")));
    }

    // --------------------------------------------------------------------------
    // RenderSlide / RenderClip / AssembleDeck
    // --------------------------------------------------------------------------

    [Fact]
    public void RenderSlideMutesAndLoopsAStillPageWithNoVoice()
    {
        var runner = new Runner(dryRun: true);
        var seg = new Segment("slide", "cover", "01.png", 4.0);
        Core.Assembly.RenderSlide(runner, seg, T("000.mp4"));
        var argv = runner.Calls[0];
        Assert.Contains("-loop", argv);
        Assert.Contains("anullsrc=channel_layout=stereo:sample_rate=48000", string.Join(" ", argv));
        Assert.Contains("scale=1920:1080", string.Join(" ", argv));
    }

    [Fact]
    public void RenderSlideMixesInTheVoiceClipWhenPresent()
    {
        var runner = new Runner(dryRun: true);
        var seg = new Segment("slide", "cover", "01.png", 4.0, Voice: "cover.wav");
        Core.Assembly.RenderSlide(runner, seg, T("000.mp4"));
        var argv = runner.Calls[0];
        Assert.Contains("cover.wav", argv);
        Assert.Contains("-filter_complex", argv);
        Assert.Contains("loudnorm", After(argv, "-filter_complex"));
    }

    [Fact]
    public void RenderClipFitsTheRecordingToTheSlideGrid()
    {
        var runner = new Runner(dryRun: true);
        var seg = new Segment("clip", "demo", "demo.mp4", 100.0);
        Core.Assembly.RenderClip(runner, seg, T("001.mp4"));
        var argv = runner.Calls[0];
        Assert.Contains("scale=1920:1080", string.Join(" ", argv));
        Assert.Contains("demo.mp4", argv);
    }

    [Fact]
    public void AssembleDeckRendersEverySegmentThenConcats()
    {
        var runner = new Runner(dryRun: true);
        var plan = new List<Segment>
        {
            new("slide", "cover", "01.png", 4.0),
            new("clip", "demo", "demo.mp4", 10.0),
            new("slide", "cierre", "02.png", 3.0),
        };
        var parts = Core.Assembly.AssembleDeck(runner, plan, _tmp, T("out.mp4"));
        Assert.Equal(3, parts.Count);
        Assert.Equal(4, runner.Calls.Count); // 3 renders + 1 concat
        Assert.Contains("concat", runner.Calls[^1]);
    }

    // --------------------------------------------------------------------------
    // tests/test_slow_pipeline.py: the one test that pays for a real encode
    // --------------------------------------------------------------------------

    /// <summary>
    /// A quiet tone, not digital silence: <c>loudnorm</c> yields NaN on all-zero input and
    /// the AAC encoder refuses it, which no real voice clip triggers.
    /// </summary>
    private static void Tone(string path, double seconds, double hz = 220.0)
    {
        const int rate = 24000;
        var count = (int)(rate * seconds);
        using var w = new BinaryWriter(File.Create(path));
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + count * 2);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(count * 2);
        for (var i = 0; i < count; i++)
            w.Write((short)(3000 * Math.Sin(2 * Math.PI * hz * i / rate)));
    }

    private static string Capture(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} failed with exit code {p.ExitCode}");
        return text;
    }

    [Fact(Skip = "needs ffmpeg")]
    public void ThreeSecondsGoThroughTheRealPipeline()
    {
        var take = T("take.mp4");
        Capture("ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            "testsrc=duration=3:size=320x180:rate=10", "-pix_fmt", "yuv420p", take);
        var marks = T("marks.json");
        File.WriteAllText(marks, Json.Dump(new JsonArray
        {
            new JsonObject { ["t"] = 0.0, ["key"] = "scene:a:start", ["label"] = "A" },
            new JsonObject { ["t"] = 1.5, ["key"] = "scene:a:end", ["label"] = "A" },
            new JsonObject { ["t"] = 1.5, ["key"] = "scene:b:start", ["label"] = "B" },
            new JsonObject { ["t"] = 3.0, ["key"] = "scene:b:end", ["label"] = "B" },
        }), new System.Text.UTF8Encoding(false));
        var voices = T("voices");
        Directory.CreateDirectory(voices);
        Tone(Path.Combine(voices, "scene-a.wav"), 2.5); // longer than its scene: the frame freezes

        var runner = new Runner();
        var outPath = T("out.mp4");
        var table = Core.Assembly.AssembleMarks(runner, take, marks, voices, outPath);

        Assert.True(File.Exists(outPath) && new FileInfo(outPath).Length > 0);
        Assert.Equal(new[] { "a", "b" }, table.Select(row => row.Name));
        Assert.Equal(4.0, runner.ProbeDuration(outPath), 0.3); // 2.5 + 1.5
        // loudnorm resamples to 192 kHz internally; the AAC that comes out must be back at
        // 48 kHz, or the Windows decoder (Films & TV, Media Player, Teams) plays it silent.
        var rate = Capture("ffprobe", "-v", "error", "-select_streams", "a", "-show_entries",
            "stream=sample_rate", "-of", "csv=p=0", outPath).Trim();
        Assert.Equal("48000", rate);
    }
}
