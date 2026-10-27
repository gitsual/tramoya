using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Tramoya.Core;

/// <summary>
/// ffmpeg/ffprobe command construction and execution (port of <c>tramoya/ffmpeg.py</c>).
/// <see cref="Runner"/> is the only thing here that starts a process; everything else is a
/// pure argv/filter builder. In dry-run mode the runner records every argv instead of
/// running it and answers duration probes from <see cref="Runner.FakeDurations"/>.
/// </summary>
public sealed class Runner
{
    public bool DryRun { get; }
    public string FfmpegPath { get; set; }
    public string FfprobePath { get; set; }
    public List<string[]> Calls { get; } = new();
    /// <summary>Keyed by the path exactly as passed to <see cref="ProbeDuration"/>.</summary>
    public Dictionary<string, double> FakeDurations { get; } = new();

    public Runner(bool dryRun = false, string ffmpeg = "ffmpeg", string ffprobe = "ffprobe")
    {
        DryRun = dryRun;
        FfmpegPath = ffmpeg;
        FfprobePath = ffprobe;
    }

    /// <summary>
    /// Run <paramref name="argv"/> (argv[0] is "ffmpeg" or "ffprobe" and is resolved through
    /// <see cref="FfmpegPath"/>/<see cref="FfprobePath"/>), or just record it in dry-run mode.
    /// </summary>
    public void Run(IReadOnlyList<string> argv)
    {
        var arr = argv.ToArray();
        Calls.Add(arr);
        if (DryRun) return;
        var exe = arr[0] switch { "ffmpeg" => FfmpegPath, "ffprobe" => FfprobePath, var other => other };
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arr.Skip(1)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{Shell.Join(arr)} failed with exit code {p.ExitCode}");
    }

    /// <summary>Duration of a media file in seconds. ffprobe first; without it, ffmpeg's own "Duration:" line.</summary>
    public double ProbeDuration(string path)
    {
        if (DryRun) return FakeDurations.TryGetValue(path, out var d) ? d : 0.0;
        if (Available(FfprobePath))
        {
            var psi = new ProcessStartInfo(FfprobePath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            };
            foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (p.ExitCode == 0 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                return seconds;
        }
        return DurationFromFfmpeg(path);
    }

    private double DurationFromFfmpeg(string path)
    {
        var psi = new ProcessStartInfo(FfmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-hide_banner", "-i", path }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        var m = Regex.Match(err, @"Duration: (\d+):(\d+):([\d.]+)");
        if (!m.Success) throw new InvalidOperationException($"cannot tell how long {path} is");
        return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
               + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Whether the configured ffmpeg can be found (absolute path or on PATH).</summary>
    public bool Available() => Available(FfmpegPath);

    public static bool Available(string exe)
    {
        if (Path.IsPathRooted(exe)) return File.Exists(exe);
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", "" } : new[] { "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            foreach (var ext in exts)
                if (File.Exists(Path.Combine(dir, exe + ext))) return true;
        }
        return false;
    }
}

/// <summary>Pure ffmpeg argv and filter builders; every string here is byte-for-byte what the Python code emits.</summary>
public static class Ffmpeg
{
    /// <summary>loudnorm upsamples to 192 kHz internally; without the aresample the AAC goes out at 96 kHz, which Windows plays as silence.</summary>
    public const string Loudnorm = "loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000";

    private const string DrawtextFontColor = "white";
    private const int DrawtextFontSize = 42;

    /// <summary>Scale-then-letterbox filter fitting any source into a fixed grid.</summary>
    public static string PadToGrid(int width, int height, string color = "0x14181F") =>
        $"scale={width}:{height}:force_original_aspect_ratio=decrease," +
        $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color={color},format=yuv420p";

    internal static string EscapeDrawtext(string label) => label.Replace("'", "\u2019").Replace(":", "\\:");

    /// <summary>A setpts+drawtext filter that speeds up a wait and captions it.</summary>
    public static string WaitCompress(double factor, string label) =>
        $"setpts=PTS/{Fmt.Py(factor)}," +
        $"drawtext=text='{EscapeDrawtext(label)}':fontcolor={DrawtextFontColor}:" +
        $"fontsize={DrawtextFontSize}:x=(w-text_w)/2:y=h-th-40:" +
        "box=1:boxcolor=black@0.5:boxborderw=12";

    /// <summary>A tpad filter cloning the last frame for <paramref name="seconds"/>.</summary>
    public static string FreezeTail(double seconds) =>
        $"tpad=stop_mode=clone:stop_duration={Fmt.F(seconds, 3)}";

    /// <summary>An anullsrc lavfi input fragment producing silence.</summary>
    public static string[] SilentAudio(string layout = "mono", int rate = 48000) =>
        new[] { "-f", "lavfi", "-i", $"anullsrc=channel_layout={layout}:sample_rate={rate}" };

    /// <summary>A concat-demuxer command stitching the paths listed in <paramref name="listPath"/> into <paramref name="outPath"/>.</summary>
    public static string[] ConcatArgv(string listPath, string outPath) =>
        new[] { "ffmpeg", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy", outPath };

    /// <summary>Write a concat-demuxer list file, one quoted <c>file</c> line per path.</summary>
    public static void WriteConcatList(IEnumerable<string> paths, string listPath)
    {
        var text = string.Concat(paths.Select(p => $"file '{p}'\n"));
        File.WriteAllText(listPath, text, new System.Text.UTF8Encoding(false));
    }
}

/// <summary>Number formatting that matches Python where the argv or a file must be identical.</summary>
public static class Fmt
{
    /// <summary>Python <c>str(x)</c> for a float: <c>1.0</c>, <c>2.5</c>, <c>0.05</c>, <c>12.333333333333334</c>.</summary>
    public static string Py(double x)
    {
        if (double.IsInteger(x) && Math.Abs(x) < 1e16) return x.ToString("F1", CultureInfo.InvariantCulture);
        var r = x.ToString("R", CultureInfo.InvariantCulture);
        if (r.Contains('E'))
            r = x.ToString("0.################", CultureInfo.InvariantCulture);
        return r;
    }

    /// <summary>Python <c>f"{x:.3f}"</c> and friends (invariant culture, fixed decimals).</summary>
    public static string F(double x, int decimals) => x.ToString("F" + decimals, CultureInfo.InvariantCulture);
}

/// <summary>Python's <c>shlex</c>, for dry-run output that people diff against the Python CLI.</summary>
public static class Shell
{
    private static readonly Regex Safe = new(@"^[\w@%+=:,./-]+$", RegexOptions.Compiled);

    public static string Quote(string s)
    {
        if (s.Length == 0) return "''";
        if (Safe.IsMatch(s)) return s;
        return "'" + s.Replace("'", "'\"'\"'") + "'";
    }

    public static string Join(IEnumerable<string> argv) => string.Join(" ", argv.Select(Quote));

    /// <summary>Python's <c>shlex.split</c> (POSIX mode), enough for a <c>launch</c> command line.</summary>
    public static List<string> Split(string command)
    {
        var result = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inWord = false, inSingle = false, inDouble = false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (inSingle) { if (c == '\'') inSingle = false; else cur.Append(c); continue; }
            if (inDouble)
            {
                if (c == '"') inDouble = false;
                else if (c == '\\' && i + 1 < command.Length && "\"\\$`\n".Contains(command[i + 1])) cur.Append(command[++i]);
                else cur.Append(c);
                continue;
            }
            if (char.IsWhiteSpace(c)) { if (inWord) { result.Add(cur.ToString()); cur.Clear(); inWord = false; } continue; }
            inWord = true;
            if (c == '\'') inSingle = true;
            else if (c == '"') inDouble = true;
            else if (c == '\\' && i + 1 < command.Length) cur.Append(command[++i]);
            else cur.Append(c);
        }
        if (inWord) result.Add(cur.ToString());
        return result;
    }
}
