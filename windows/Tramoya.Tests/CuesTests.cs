using Tramoya.Core;

namespace Tramoya.Tests;

/// <summary>Port of <c>tests/test_cues.py</c>: parsing recorded cue files and rendering ASS subtitles.</summary>
public class CuesTests
{
    private const string CuesText = "0.000|opening line\n4.120|second line\n9.500|";

    [Fact]
    public void ParseCuesIgnoresTheEmptyTextSentinel()
    {
        var cues = Cues.Parse(CuesText);
        Assert.Equal(new[] { new Cue(0.0, "opening line"), new Cue(4.12, "second line") }, cues);
    }

    [Fact]
    public void ParseCuesWithTotalReturnsTheSentinelTimestamp()
    {
        var (cues, total) = Cues.ParseWithTotal(CuesText);
        Assert.Equal(2, cues.Count);
        Assert.Equal(9.5, total);
    }

    [Fact]
    public void ParseCuesWithTotalDefaultsTotalWhenNoSentinel()
    {
        var (cues, total) = Cues.ParseWithTotal("0.000|only line");
        Assert.Single(cues);
        Assert.Equal(0.0, total);
    }

    [Fact]
    public void ParseCuesSkipsBlankLines()
    {
        var cues = Cues.Parse("0.000|a\n\n1.000|b\n");
        Assert.Equal(new[] { "a", "b" }, cues.Select(c => c.Text));
    }

    [Fact]
    public void ParseMetaReadsKeyValuePairs()
    {
        var meta = Cues.ParseMeta("resolution=1920x1080\nelapsed=42.5\n");
        Assert.Equal(new Dictionary<string, string> { ["resolution"] = "1920x1080", ["elapsed"] = "42.5" }, meta);
    }

    [Fact]
    public void OffsetFromDurationIsTheGapBetweenDurationAndElapsed()
    {
        Assert.Equal(7.5, Cues.OffsetFromDuration(duration: 50.0, elapsed: 42.5));
    }

    [Fact]
    public void OffsetFromDurationNeverGoesNegative()
    {
        Assert.Equal(0.0, Cues.OffsetFromDuration(duration: 10.0, elapsed: 42.5));
    }

    [Fact]
    public void ShiftAddsTheOffsetToEveryCue()
    {
        var cues = new List<Cue> { new(0.0, "a"), new(1.0, "b") };
        var shifted = Cues.Shift(cues, 2.5);
        Assert.Equal(new[] { new Cue(2.5, "a"), new Cue(3.5, "b") }, shifted);
        // original list is untouched
        Assert.Equal(new[] { new Cue(0.0, "a"), new Cue(1.0, "b") }, cues);
    }

    [Fact]
    public void CuesToMarksTurnsEachCueIntoASceneWithTheNextCuesStartAsItsEnd()
    {
        var cues = new List<Cue> { new(0.0, "a"), new(1.0, "b"), new(3.0, "c") };
        var marks = Cues.ToMarks(cues);
        Assert.Equal(
            new[] { "scene:0:start", "scene:0:end", "scene:1:start", "scene:1:end", "scene:2:start" },
            marks.Select(m => m.Key));
        Assert.Equal(0.0, marks[0].T);
        Assert.Equal(1.0, marks[1].T);
        Assert.Equal("a", marks[0].Label);
        Assert.Equal("a", marks[1].Label);
    }

    [Fact]
    public void CuesToMarksOfEmptyListIsEmpty()
    {
        Assert.Empty(Cues.ToMarks(new List<Cue>()));
    }

    [Fact]
    public void CuesToAssContainsStyleAndDialogueLines()
    {
        var cues = new List<Cue> { new(0.0, "hello"), new(5.0, "world") };
        var ass = Cues.ToAss(cues, total: 10.0, width: 1920, height: 1080);
        Assert.Contains("[Script Info]", ass);
        Assert.Contains("PlayResX: 1920", ass);
        Assert.Contains("PlayResY: 1080", ass);
        Assert.DoesNotContain("BorderStyle=3", ass); // style values are positional, not key=value
        Assert.Contains("\\fad(300,300)", ass);
        Assert.Contains("hello", ass);
        Assert.Contains("world", ass);
    }

    private static List<string> Dialogue(string ass) =>
        ass.Split('\n').Where(line => line.StartsWith("Dialogue:", StringComparison.Ordinal)).ToList();

    [Fact]
    public void CuesToAssEndsEachLineBeforeTheNextCueStarts()
    {
        var cues = new List<Cue> { new(0.0, "hello"), new(5.0, "world") };
        var ass = Cues.ToAss(cues, total: 10.0, width: 1920, height: 1080);
        var dialogue = Dialogue(ass);
        Assert.Equal(2, dialogue.Count);
        // "hello" ends 0.15s before "world" starts at 0:00:05.00
        Assert.Contains("0:00:04.85", dialogue[0]);
    }

    [Fact]
    public void CuesToAssLastLineEndsAtTotal()
    {
        var cues = new List<Cue> { new(0.0, "hello") };
        var ass = Cues.ToAss(cues, total: 5.0, width: 1920, height: 1080);
        var dialogue = Dialogue(ass);
        Assert.Single(dialogue);
        Assert.Contains("0:00:04.85", dialogue[0]);
    }

    [Fact]
    public void CuesToAssSkipsEmptyTextCues()
    {
        var cues = new List<Cue> { new(0.0, "hello"), new(5.0, "") };
        var ass = Cues.ToAss(cues, total: 5.0, width: 1920, height: 1080);
        Assert.Single(Dialogue(ass));
    }

    [Fact]
    public void CuesToAssScalesFontSizeAndMarginToHeight()
    {
        var cues = new List<Cue> { new(0.0, "hi") };
        var ass = Cues.ToAss(cues, total: 1.0, width: 1920, height: 300);
        // size = max(20, height // 30) = 20; margin = height // 16 = 18
        var styleLine = ass.Split('\n').First(line => line.StartsWith("Style:", StringComparison.Ordinal));
        var fields = styleLine.Split(',');
        Assert.Equal("20", fields[2]);
    }
}
