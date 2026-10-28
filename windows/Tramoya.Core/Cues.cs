using System.Text;
using System.Text.RegularExpressions;

namespace Tramoya.Core;

/// <summary>A timestamped narration line said live during a take.</summary>
public sealed record Cue(double T, string Text);

/// <summary>
/// Cue files (port of <c>tramoya/cues.py</c>): timestamped narration lines written live
/// during a take, and their conversion into scene marks and burned-in ASS subtitles.
/// <para>
/// A cue file is one <c>timestamp|text</c> line per shot, in the order they were said.
/// The final line is a sentinel: its text is empty and its timestamp is the take's total
/// elapsed time, so a reader never has to guess where the take ended from the last
/// spoken line alone.
/// </para>
/// </summary>
public static class Cues
{
    private const double TrailingGapSeconds = 5.0;
    private const int FadeMs = 300;
    private const double EndTrimSeconds = 0.15;

    private static readonly Regex LineBreak = new(@"\r\n|\r|\n", RegexOptions.Compiled);
    private static readonly Regex MetaLine = new(@"^(\w+)=(.*)$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Parse a cue file's text into its cues and the sentinel total. The sentinel line
    /// (empty text) is not returned as a cue; its timestamp becomes <c>Total</c>. If no
    /// sentinel is present, <c>Total</c> is 0.0.
    /// </summary>
    public static (List<Cue> Cues, double Total) ParseWithTotal(string text)
    {
        var cues = new List<Cue>();
        var total = 0.0;
        foreach (var line in LineBreak.Split(text))
        {
            if (line.Length == 0) continue;
            var bar = line.IndexOf('|');
            var at = bar < 0 ? line : line[..bar];
            var body = bar < 0 ? "" : line[(bar + 1)..];
            if (!double.TryParse(at, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var t))
                throw new ArgumentException($"could not convert string to float: '{at}'");
            if (body.Length > 0) cues.Add(new Cue(t, body));
            else total = t;
        }
        return (cues, total);
    }

    /// <summary>Parse a cue file's text into its cues, ignoring the sentinel line.</summary>
    public static List<Cue> Parse(string text) => ParseWithTotal(text).Cues;

    /// <summary>Parse a <c>key=value</c> per line meta file into a dictionary (a repeated key keeps its last value).</summary>
    public static Dictionary<string, string> ParseMeta(string text)
    {
        var meta = new Dictionary<string, string>();
        foreach (Match m in MetaLine.Matches(text))
            meta[m.Groups[1].Value] = m.Groups[2].Value;
        return meta;
    }

    /// <summary>
    /// How much pre-roll happened before the cue clock started. The cue clock starts after
    /// the recorder is already writing, so every cue is late by exactly that pre-roll,
    /// which the finished file's own duration gives away and which is added back to every
    /// cue's timestamp.
    /// </summary>
    public static double OffsetFromDuration(double duration, double elapsed) =>
        Math.Max(0.0, duration - elapsed);

    /// <summary>A new list with <paramref name="offset"/> added to every cue's timestamp.</summary>
    public static List<Cue> Shift(IEnumerable<Cue> cues, double offset) =>
        cues.Select(c => new Cue(c.T + offset, c.Text)).ToList();

    /// <summary>
    /// Turn a cue sequence into scene marks: cue <c>i</c> starts scene <c>i</c>, and the
    /// next cue's timestamp closes it. The last cue only opens its scene: there is nothing
    /// after it to mark where it ends.
    /// </summary>
    public static List<Mark> ToMarks(IReadOnlyList<Cue> cues)
    {
        var marks = new List<Mark>();
        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            marks.Add(new Mark(cue.T, $"scene:{index}:start", cue.Text));
            if (index + 1 < cues.Count)
                marks.Add(new Mark(cues[index + 1].T, $"scene:{index}:end", cue.Text));
        }
        return marks;
    }

    /// <summary>Python's <c>f"{int(h)}:{int(m):02d}:{s:05.2f}"</c> after a float divmod.</summary>
    private static string AssStamp(double seconds)
    {
        seconds = Math.Max(0.0, seconds);
        var hours = Math.Floor(seconds / 3600);
        seconds -= hours * 3600;
        var minutes = Math.Floor(seconds / 60);
        seconds -= minutes * 60;
        return $"{(int)hours}:{(int)minutes:00}:{Fmt.F(seconds, 2).PadLeft(5, '0')}";
    }

    /// <summary>ASS takes <c>&amp;HAABBGGRR</c>: alpha first, and the channels reversed.</summary>
    private static string AssColour(string hexRgb, string alpha = "00")
    {
        var rr = hexRgb.Substring(0, 2);
        var gg = hexRgb.Substring(2, 2);
        var bb = hexRgb.Substring(4, 2);
        return $"&H{alpha}{bb}{gg}{rr}".ToUpperInvariant();
    }

    /// <summary>
    /// Render a cue sequence as an ASS subtitle document. Each cue is a plated dialogue
    /// line (BorderStyle=3 fills a box in the outline colour, so the outline becomes
    /// padding rather than a stroke) that fades in and out and ends
    /// <see cref="EndTrimSeconds"/> before the next one starts, so two adjacent lines never
    /// overlap on screen. The text is byte-identical to the Python version.
    /// </summary>
    public static string ToAss(
        IReadOnlyList<Cue> cues,
        double total,
        int width,
        int height,
        string font = "JetBrainsMono Nerd Font",
        string fg = "D9E2DD",
        string bg = "0E1513")
    {
        var size = Math.Max(20, height / 30);
        var margin = height / 16;
        var primary = AssColour(fg);
        var outline = AssColour(bg);

        var header =
            "[Script Info]\n" +
            "ScriptType: v4.00+\n" +
            $"PlayResX: {width}\n" +
            $"PlayResY: {height}\n" +
            "WrapStyle: 0\n" +
            "ScaledBorderAndShadow: yes\n" +
            "\n" +
            "[V4+ Styles]\n" +
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, " +
            "OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, " +
            "ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, " +
            "MarginL, MarginR, MarginV, Encoding\n" +
            $"Style: Narration,{font},{size},{primary},{primary},{outline},{outline}," +
            $"0,0,0,0,100,100,0,0,3,6,0,2,{margin},{margin},{margin},1\n" +
            "\n" +
            "[Events]\n" +
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, " +
            "Effect, Text\n";

        var lines = new List<string>();
        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            if (cue.Text.Length == 0) continue;
            var end = index + 1 < cues.Count
                ? cues[index + 1].T
                : Math.Max(total, cue.T + TrailingGapSeconds);
            lines.Add(
                $"Dialogue: 0,{AssStamp(cue.T)},{AssStamp(end - EndTrimSeconds)}," +
                $"Narration,,0,0,0,,{{\\fad({FadeMs},{FadeMs})}}{cue.Text}");
        }

        var sb = new StringBuilder(header);
        sb.Append(string.Join("\n", lines));
        if (lines.Count > 0) sb.Append('\n');
        return sb.ToString();
    }
}
