using Tramoya.Core;

namespace Tramoya.Tests;

/// <summary>
/// Port of <c>tests/test_ffmpeg.py</c>. The Python suite monkeypatches <c>subprocess</c>;
/// here the live-process tests use <c>cmd.exe</c> and a throwaway <c>ffprobe.cmd</c> that
/// answers like ffprobe, so nothing needs the real binaries.
/// </summary>
public sealed class FfmpegTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tramoya-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A stand-in ffprobe: logs its argv to <c>seen.txt</c> and prints a duration.</summary>
    private static string FakeFfprobe(string dir, string stdout)
    {
        var path = Path.Combine(dir, "ffprobe.cmd");
        File.WriteAllText(path, "@echo off\r\necho %* > \"%~dp0seen.txt\"\r\necho " + stdout + "\r\n");
        return path;
    }

    private static string[] SeenArgv(string dir) =>
        File.ReadAllText(Path.Combine(dir, "seen.txt")).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void LoudnormMatchesTheShippedNormalisation()
    {
        Assert.Equal("loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000", Ffmpeg.Loudnorm);
    }

    [Fact]
    public void RunnerDryRunRecordsCallsWithoutShellingOut()
    {
        // A binary that cannot exist: if dry-run ever shelled out, Run would throw.
        var runner = new Runner(dryRun: true, ffmpeg: Path.Combine(TempDir(), "no-such-ffmpeg.exe"));
        runner.Run(new[] { "ffmpeg", "-y", "-i", "in.mp4", "out.mp4" });
        Assert.Equal(new[] { new[] { "ffmpeg", "-y", "-i", "in.mp4", "out.mp4" } }, runner.Calls);
    }

    [Fact]
    public void RunnerLiveRunShellsOutWithCheck()
    {
        var runner = new Runner(dryRun: false);
        runner.Run(new[] { "cmd.exe", "/c", "exit 0" });
        Assert.Equal(new[] { new[] { "cmd.exe", "/c", "exit 0" } }, runner.Calls);

        // check=True: a failing command is an exception, not a silent bad take.
        var ex = Assert.Throws<InvalidOperationException>(() => runner.Run(new[] { "cmd.exe", "/c", "exit 3" }));
        Assert.Contains("exit code 3", ex.Message);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public void ProbeDurationInDryRunReadsFakeDurations()
    {
        var runner = new Runner(dryRun: true);
        runner.FakeDurations["scene-1.mp4"] = 12.5;
        Assert.Equal(12.5, runner.ProbeDuration("scene-1.mp4"));
    }

    [Fact]
    public void ProbeDurationInDryRunDefaultsToZeroForUnknownPaths()
    {
        Assert.Equal(0.0, new Runner(dryRun: true).ProbeDuration("missing.mp4"));
    }

    [Fact]
    public void ProbeDurationLiveShellsOutToFfprobe()
    {
        var dir = TempDir();
        var runner = new Runner(dryRun: false, ffprobe: FakeFfprobe(dir, "3.140000"));
        Assert.Equal(3.14, runner.ProbeDuration("clip.mp4"), 6);
        Assert.Equal("clip.mp4", SeenArgv(dir)[^1]);
    }

    [Fact]
    public void AvailableChecksForTheFfmpegBinary()
    {
        Assert.True(new Runner(ffmpeg: "cmd").Available());
        Assert.True(new Runner(ffmpeg: FakeFfprobe(TempDir(), "0")).Available());
        Assert.False(new Runner(ffmpeg: "tramoya-no-such-binary").Available());
        Assert.False(new Runner(ffmpeg: Path.Combine(TempDir(), "ffmpeg.exe")).Available());
    }

    [Fact]
    public void PadToGridScalesAndLetterboxesToTheTargetSize()
    {
        Assert.Equal(
            "scale=1920:1080:force_original_aspect_ratio=decrease," +
            "pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=0x14181F,format=yuv420p",
            Ffmpeg.PadToGrid(1920, 1080));
    }

    [Fact]
    public void PadToGridAcceptsACustomBackgroundColour()
    {
        var vf = Ffmpeg.PadToGrid(640, 480, color: "0x000000");
        Assert.Contains("color=0x000000", vf);
        Assert.StartsWith("scale=640:480:", vf);
    }

    [Fact]
    public void WaitCompressBuildsSetptsAndDrawtext()
    {
        var vf = Ffmpeg.WaitCompress(5.0, "4 minutes later");
        Assert.StartsWith("setpts=PTS/5.0,", vf);
        Assert.Contains("drawtext=text='4 minutes later'", vf);
        Assert.Contains("fontcolor=0xF0CB66:fontsize=42", vf);
    }

    [Fact]
    public void WaitCompressEscapesApostrophesAndColonsInTheLabel()
    {
        var vf = Ffmpeg.WaitCompress(2.0, "it's 12:30");
        Assert.Contains("text='it’s 12\\:30'", vf);
    }

    [Fact]
    public void FreezeTailBuildsATpadCloneFilter()
    {
        Assert.Equal("tpad=stop_mode=clone:stop_duration=1.500", Ffmpeg.FreezeTail(1.5));
    }

    [Fact]
    public void SilentAudioDefaultsToMono48k()
    {
        Assert.Equal(new[] { "-f", "lavfi", "-i", "anullsrc=channel_layout=mono:sample_rate=48000" }, Ffmpeg.SilentAudio());
    }

    [Fact]
    public void SilentAudioAcceptsLayoutAndRate()
    {
        Assert.Equal(new[] { "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=44100" },
            Ffmpeg.SilentAudio(layout: "stereo", rate: 44100));
    }

    [Fact]
    public void ConcatArgvBuildsTheConcatDemuxerCommand()
    {
        Assert.Equal(
            new[] { "ffmpeg", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", "list.txt", "-c", "copy", "out.mp4" },
            Ffmpeg.ConcatArgv("list.txt", "out.mp4"));
    }

    [Fact]
    public void WriteConcatListWritesOneQuotedFileLinePerPath()
    {
        var listPath = Path.Combine(TempDir(), "concat.txt");
        Ffmpeg.WriteConcatList(new[] { "a.mp4", "b.mp4" }, listPath);
        Assert.Equal("file 'a.mp4'\nfile 'b.mp4'\n", File.ReadAllText(listPath));
    }

    [Fact]
    public void ProbeDurationUsesTheCsvShapeFfprobeAccepts()
    {
        var dir = TempDir();
        var runner = new Runner(ffprobe: FakeFfprobe(dir, "1.500000"));
        Assert.Equal(1.5, runner.ProbeDuration("x.mp4"));
        Assert.Equal(new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", "x.mp4" }, SeenArgv(dir));
    }
}
