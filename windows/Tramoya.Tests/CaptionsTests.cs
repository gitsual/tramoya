using Tramoya.Core;
using Xunit;

namespace Tramoya.Tests;

public class CaptionsTests
{
    private sealed record Seg(string Kind, string Name, double Duration) : ISegment;
    private static string Temp() { string p = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(p); return p; }
    [Fact] public void SplitSentencesKeepsTerminalPunctuation() => Assert.Equal(new[] { "Uno.", "Dos!", "Tres?" }, Captions.SplitSentences("Uno. Dos! Tres?"));
    [Fact] public void SplitLongKeepsEveryWord()
    {
        const string s = "The risk is not in using artificial intelligence, it is in wiring it to Jira, Git and Bitbucket without clear gates and without audit.";
        var pieces = Captions.SplitLong(s); Assert.True(pieces.Count > 1); Assert.Equal(s.Split(' '), string.Join(" ", pieces).Split(' '));
    }
    [Fact] public void SplitLongLeavesNoOneWordTail() => Assert.True(Captions.SplitLong(string.Concat(Enumerable.Repeat("a ", 60)) + "final.")[^1].Length > 84 / 4);
    [Fact] public void WrapReturnsTextUnchangedWhenItFits() => Assert.Equal("short line", Captions.Wrap("short line"));
    [Fact] public void WrapBreaksAtTheMostEvenWordBoundary()
    {
        const string text = "one two three four five six seven eight nine ten eleven twelve";
        var lines = Captions.Wrap(text).Split('\n'); Assert.Equal(2, lines.Length); Assert.True(Captions.Fits(text) || lines.Max(l => l.Length) <= 42);
    }
    [Fact] public void PageCuesLoseNoNarration()
    {
        const string text = "This is the first sentence. This is the second, a bit longer one.";
        Assert.Equal(text.Split(' '), string.Join(" ", Captions.PageCues(text, 0, 10).Select(c => c.Text.Replace('\n', ' '))).Split(' '));
    }
    [Fact] public void PageCuesCoverTheWholeWindowWithoutOverlap()
    {
        var cues = Captions.PageCues("First sentence here. Second sentence follows. Third one closes it.", 10, 40);
        Assert.Equal(10, cues[0].Start); Assert.Equal(40, cues[^1].End);
        for (int i = 1; i < cues.Count; i++) Assert.Equal(cues[i - 1].End, cues[i].Start, 8);
    }
    [Fact] public void PageCuesReturnsNothingForBlankText() => Assert.Empty(Captions.PageCues("   ", 0, 5));
    [Fact] public void BuildCuesScalesToTheMeasuredRuntime()
    {
        var cues = Captions.BuildCues([("cover", "One sentence."), ("agenda", "Another sentence.")], [new Seg("slide", "cover", 10), new Seg("slide", "agenda", 10)], scale: .5);
        Assert.Equal(0, cues[0].Start); Assert.Equal(10, cues[^1].End, 8);
    }
    [Fact] public void BuildCuesGivesTheRecordedClipItsOwnCue()
    {
        var cues = Captions.BuildCues([("video", "One sentence.")], [new Seg("slide", "video", 10), new Seg("clip", "demo", 145)], "Recorded demo, real system.");
        var clip = Assert.Single(cues, c => c.Text.Replace('\n', ' ').Contains("Recorded demo"));
        Assert.Equal(10, clip.Start, 8); Assert.True(clip.End - clip.Start <= 4); Assert.True(cues[^1].End <= 155);
    }
    [Fact] public void BuildCuesSkipsPagesTheScriptDoesNotMention() => Assert.Empty(Captions.BuildCues([], [new Seg("slide", "cover", 5)]));
    [Fact] public void TimestampIsSrtShaped() { Assert.Equal("00:00:00,000", Captions.Timestamp(0)); Assert.Equal("00:11:13,808", Captions.Timestamp(673.808)); }
    [Fact] public void RenderSrtNumbersCuesFromOne()
    {
        var output = Captions.RenderSrt([new(0, 1, "one"), new(1, 2, "two")]);
        Assert.Equal("1\n00:00:00,000 --> 00:00:01,000\none\n\n2\n00:00:01,000 --> 00:00:02,000\ntwo\n", output);
    }
    [Fact] public void RenderAssHasTheVivacPlateStyle()
    {
        string ass = Captions.RenderAss([new(0, 2, "hello")], 1920, 1080);
        foreach (var section in new[] { "[Script Info]", "[V4+ Styles]", "[Events]" }) Assert.Contains(section, ass);
        var fields = ass.Split('\n').Single(l => l.StartsWith("Style: Narration")).Split(',');
        Assert.Equal("JetBrainsMono Nerd Font", fields[1]); Assert.Equal("36", fields[2]);
        Assert.Equal(new[] { "3", "6", "0", "2" }, fields[15..19]); Assert.Equal(new[] { "68", "68", "68" }, fields[19..22]);
    }
    [Fact] public void RenderAssColoursAreAabbggrrWithAlphaFirst()
    {
        var ass = Captions.RenderAss([new(0, 1, "x")], 1920, 1080, fg: "D9E2DD", bg: "0E1513"); Assert.Contains("&H00DDE2D9", ass); Assert.Contains("&H0013150E", ass);
    }
    [Fact] public void RenderAssDialogueFadesAndTrimsTheTail() => Assert.Equal("Dialogue: 0,0:00:01.00,0:00:02.85,Narration,,0,0,0,,{\\fad(300,300)}hi", Captions.RenderAss([new(1, 3, "hi")], 1920, 1080).Split('\n').Single(l => l.StartsWith("Dialogue:")));
    [Fact] public void BurnArgvMatchesTheRecordedDryRun() => Assert.Equal(new[] { "ffmpeg", "-y", "-i", "out/tour-silent.mp4", "-vf", "subtitles=out/tour.srt", "-c:a", "copy", "out/tour-captions.mp4" }, Captions.BurnArgv("out/tour-silent.mp4", "out/tour.srt", "out/tour-captions.mp4"));
    [Fact] public void TrimPlanCutsEachRangeThenConcats()
    {
        string dir = Temp(); try { var args = Captions.TrimPlan([(0, 120), (600, 780)], "in.mp4", dir, "out.mp4"); Assert.Equal(3, args.Count); Assert.Equal("ffmpeg", args[0][0]); Assert.Contains("-ss 0 -to 120", string.Join(" ", args[0])); Assert.Contains("-ss 600 -to 780", string.Join(" ", args[1])); Assert.Contains("-f", args[2]); Assert.Contains("concat", args[2]); } finally { Directory.Delete(dir, true); }
    }
    [Fact] public void TrimPlanWritesAConcatListOfTheTrimmedParts()
    {
        string dir = Temp(); try { Captions.TrimPlan([(0, 10), (10, 20)], "in.mp4", dir, "out.mp4"); string list = Assert.Single(Directory.GetFiles(dir, "*concat*.txt")); Assert.Equal(2, File.ReadAllText(list).Split("file '").Length - 1); } finally { Directory.Delete(dir, true); }
    }
}
