using System.Globalization;
using System.Text.RegularExpressions;

namespace Tramoya.Core;

public interface ISegment
{
    string Kind { get; }
    string Name { get; }
    double Duration { get; }
}

public sealed record CaptionCue(double Start, double End, string Text);

/// <summary>Subtitle timing apportioned by narration length, with SRT/ASS rendering and trim commands.</summary>
public static class Captions
{
    public const int MaxChars = 84, LineChars = 42, MinChunk = 28;
    private static int Length(string s) => s.EnumerateRunes().Count();
    private static string[] Words(string s) => Regex.Split(s.Trim(), @"\s+").Where(x => x.Length > 0).ToArray();
    public static List<string> SplitSentences(string text) => Regex.Split(text.Trim(), @"(?<=[.:?!])\s+").Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    public static string Wrap(string text, int width = LineChars)
    {
        if (Length(text) <= width) return text;
        var words = Words(text);
        int best = 1, worst = int.MaxValue;
        for (int i = 1; i < words.Length; i++)
        {
            int candidate = Math.Max(Length(string.Join(" ", words.Take(i))), Length(string.Join(" ", words.Skip(i))));
            if (candidate < worst) { best = i; worst = candidate; }
        }
        return string.Join(" ", words.Take(best)) + "\n" + string.Join(" ", words.Skip(best));
    }
    public static bool Fits(string text) => Wrap(text).Split('\n').All(x => Length(x) <= LineChars);
    public static List<string> SplitLong(string sentence, int limit = MaxChars)
    {
        if (Length(sentence) <= limit && Fits(sentence)) return [sentence];
        int parts = (Length(sentence) + limit - 1) / limit;
        double target = (double)Length(sentence) / parts;
        var pieces = new List<string>();
        string current = "";
        foreach (var word in Words(sentence))
        {
            var candidate = (current + " " + word).Trim();
            if (current.Length > 0 && pieces.Count < parts - 1 && (Length(candidate) > limit || Length(current) >= target))
            { pieces.Add(current); current = word; }
            else current = candidate;
        }
        if (current.Length > 0) pieces.Add(current);
        return pieces.SelectMany(p => limit <= LineChars || Fits(p) ? new List<string> { p } : SplitLong(p, limit - 4)).ToList();
    }
    public static List<CaptionCue> PageCues(string text, double start, double end)
    {
        var chunks = new List<string>();
        foreach (var piece in SplitSentences(text).SelectMany(s => SplitLong(s)))
            if (chunks.Count > 0 && Length(chunks[^1]) < MinChunk && Fits(chunks[^1] + " " + piece)) chunks[^1] += " " + piece;
            else chunks.Add(piece);
        if (chunks.Count > 1 && Length(chunks[^1]) < MinChunk && Fits(chunks[^2] + " " + chunks[^1]))
        { chunks[^2] += " " + chunks[^1]; chunks.RemoveAt(chunks.Count - 1); }
        int total = chunks.Sum(Length);
        double at = start;
        var cues = new List<CaptionCue>();
        for (int i = 0; i < chunks.Count; i++)
        {
            double stop = i == chunks.Count - 1 ? end : at + (end - start) * Length(chunks[i]) / total;
            cues.Add(new(at, stop, Wrap(chunks[i]))); at = stop;
        }
        return cues;
    }
    public static List<CaptionCue> BuildCues(IEnumerable<(string Name, string Text)> scriptLines, IEnumerable<ISegment> segments, string? clipCue = null, double scale = 1.0)
    {
        var said = new Dictionary<string, string>();
        foreach (var (name, text) in scriptLines) said[name] = text;
        var cues = new List<CaptionCue>();
        double at = 0;
        foreach (var seg in segments)
        {
            double start = at * scale, end = (at + seg.Duration) * scale; at += seg.Duration;
            if (seg.Kind == "clip") { if (!string.IsNullOrEmpty(clipCue)) cues.Add(new(start, Math.Min(start + 4, end), Wrap(clipCue))); }
            else if (said.TryGetValue(seg.Name, out var text) && text.Length > 0) cues.AddRange(PageCues(text, start, end));
        }
        return cues;
    }
    public static string Timestamp(double seconds)
    {
        long ms = (long)Math.Round(seconds * 1000);
        long h = (long)Math.Floor(ms / 3600000.0); ms -= h * 3600000;
        long m = ms / 60000; ms %= 60000;
        return FormattableString.Invariant($"{h:00}:{m:00}:{ms / 1000:00},{ms % 1000:000}");
    }
    public static string RenderSrt(IEnumerable<CaptionCue> cues) => string.Join("\n", cues.Select((c, i) => $"{i + 1}\n{Timestamp(c.Start)} --> {Timestamp(c.End)}\n{c.Text}\n"));
    private static double Mod(double value, double divisor) { double r = value % divisor; return r < 0 ? r + divisor : r; }
    private static string AssStamp(double s) => FormattableString.Invariant($"{Math.Floor(s / 3600):0}:{Math.Floor(Mod(s, 3600) / 60):00}:{Mod(s, 60):00.00}");
    private static string Colour(string rgb) => ("&H00" + rgb[4..6] + rgb[2..4] + rgb[..2]).ToUpperInvariant();
    public static string RenderAss(IEnumerable<CaptionCue> cues, int width, int height, string font = "JetBrainsMono Nerd Font", string fg = "D9E2DD", string bg = "0E1513")
    {
        var size = Math.Max(20, Math.Round(height / 30.0)); var margin = Math.Round(height / 16.0);
        var header = FormattableString.Invariant($"[Script Info]\nScriptType: v4.00+\nPlayResX: {width}\nPlayResY: {height}\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Narration,{font},{size},{Colour(fg)},{Colour(fg)},{Colour(bg)},{Colour(bg)},0,0,0,0,100,100,0,0,3,6,0,2,{margin},{margin},{margin},1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        return header + string.Join("\n", cues.Select(c => $"Dialogue: 0,{AssStamp(c.Start)},{AssStamp(Math.Max(c.Start, c.End - .15))},Narration,,0,0,0,,{{\\fad(300,300)}}{c.Text.Replace("\n", "\\N")}")) + "\n";
    }
    public static string[] BurnArgv(string video, string subtitlesPath, string outPath) => ["ffmpeg", "-y", "-i", video, "-vf", $"subtitles={subtitlesPath}", "-c:a", "copy", outPath];
    public static List<string[]> TrimPlan(IEnumerable<(double Start, double End)> ranges, string video, string workDir, string outPath)
    {
        var argvs = new List<string[]>(); var parts = new List<string>();
        string Seconds(double x) => double.IsInteger(x) ? x.ToString("0", CultureInfo.InvariantCulture) : Fmt.Py(x);
        foreach (var (start, end) in ranges)
        {
            string part = Path.Combine(workDir, $"trim-{parts.Count}.mp4");
            argvs.Add(["ffmpeg", "-loglevel", "error", "-y", "-ss", Seconds(start), "-to", Seconds(end), "-i", video, "-c", "copy", part]); parts.Add(part);
        }
        string list = Path.Combine(workDir, "trim-concat.txt");
        Ffmpeg.WriteConcatList(parts, list); argvs.Add(Ffmpeg.ConcatArgv(list, outPath)); return argvs;
    }
}
