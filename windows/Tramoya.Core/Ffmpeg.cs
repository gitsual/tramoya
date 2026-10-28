using System.Diagnostics;
using System.Globalization;
using System.Numerics;
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

    private const string DrawtextFontColor = "0xF0CB66";
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
        "box=1:boxcolor=0x1F1E1C@0.92:boxborderw=16";

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
    /// <summary>
    /// Python <c>repr(x)</c> / <c>str(x)</c> for a float: <c>1.0</c>, <c>2.5</c>, <c>0.05</c>,
    /// <c>12.333333333333334</c>, <c>1e-05</c>, <c>1e+16</c>, <c>inf</c>, <c>nan</c>.
    ///
    /// Both runtimes find the shortest digit string that round-trips; they differ only in
    /// how they spell it. Python switches to exponent notation below 1e-4 and from 1e16 up
    /// (the decimal point would otherwise sit before the first digit by more than three
    /// zeros, or past the 16th digit), writes the exponent with a sign and two digits at
    /// least, and always keeps a <c>.0</c> on a whole number written in fixed notation.
    /// </summary>
    public static string Py(double x)
    {
        if (double.IsNaN(x)) return "nan";
        if (double.IsPositiveInfinity(x)) return "inf";
        if (double.IsNegativeInfinity(x)) return "-inf";
        if (x == 0) return double.IsNegative(x) ? "-0.0" : "0.0";

        var (digits, decpt) = ShortestDigits(Math.Abs(x));
        var sign = x < 0 ? "-" : "";
        if (decpt <= -4 || decpt > 16)
        {
            var mantissa = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
            var exp = decpt - 1;
            return sign + mantissa + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
        }
        if (decpt <= 0) return sign + "0." + new string('0', -decpt) + digits;
        if (decpt >= digits.Length) return sign + digits + new string('0', decpt - digits.Length) + ".0";
        return sign + digits[..decpt] + "." + digits[decpt..];
    }

    /// <summary>
    /// The shortest round-trip digits of a positive double, without leading or trailing
    /// zeros, and the position of the decimal point relative to them (<c>0.0001</c> is
    /// <c>("1", -3)</c>; <c>123.45</c> is <c>("12345", 3)</c>). The "R" format already
    /// produces those digits on .NET Core 3+; this only reads them back out of its text.
    /// </summary>
    private static (string Digits, int DecimalPoint) ShortestDigits(double positive)
    {
        var text = positive.ToString("R", CultureInfo.InvariantCulture);
        var exponent = 0;
        var e = text.IndexOf('E');
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }
        var point = text.IndexOf('.');
        if (point < 0) point = text.Length;
        var digits = text.Remove(point, point < text.Length ? 1 : 0);
        var decpt = point + exponent;
        var leading = digits.Length - digits.TrimStart('0').Length;
        digits = digits[leading..].TrimEnd('0');
        return (digits, decpt - leading);
    }

    /// <summary>
    /// Python <c>round(x, ndigits)</c> for a float. Python rounds the exact binary value of
    /// the double, half to even, which is not what <c>Math.Round(x, ndigits)</c> does:
    /// <c>2.675</c> is really 2.67499999..., so Python gives <c>2.67</c> where .NET gives
    /// <c>2.68</c>. The double is expanded exactly as <c>mantissa * 2^exponent</c>, scaled
    /// by <c>10^ndigits</c>, rounded as an integer, and the decimal result is parsed back,
    /// which is the correctly rounded conversion Python's <c>strtod</c> performs too.
    /// </summary>
    public static double PyRound(double x, int ndigits)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || x == 0) return x;
        // Python's own short-cuts: beyond these the result is x itself or a signed zero.
        if (ndigits > 323) return x;
        if (ndigits < -308) return Math.CopySign(0.0, x);

        var bits = BitConverter.DoubleToInt64Bits(x);
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0) exponent = 1; else mantissa |= 1L << 52;
        exponent -= 1075;  // |x| = mantissa * 2^exponent, exactly

        var numerator = new BigInteger(mantissa);
        var denominator = BigInteger.One;
        if (exponent >= 0) numerator <<= exponent; else denominator <<= -exponent;
        if (ndigits >= 0) numerator *= BigInteger.Pow(10, ndigits); else denominator *= BigInteger.Pow(10, -ndigits);

        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var twice = remainder * 2;
        if (twice > denominator || (twice == denominator && !quotient.IsEven)) quotient += 1;

        var scaled = quotient.ToString(CultureInfo.InvariantCulture) + "E" + (-ndigits).ToString(CultureInfo.InvariantCulture);
        return Math.CopySign(double.Parse(scaled, NumberStyles.Float, CultureInfo.InvariantCulture), x);
    }

    /// <summary>Python <c>f"{x:.3f}"</c> and friends (invariant culture, fixed decimals).</summary>
    public static string F(double x, int decimals) => x.ToString("F" + decimals, CultureInfo.InvariantCulture);
}

/// <summary>Python's <c>shlex</c>, for dry-run output that people diff against the Python CLI.</summary>
public static class Shell
{
    // ECMAScript keeps \w ASCII-only, as shlex.quote's re.ASCII does: "año" must come out quoted.
    private static readonly Regex Safe = new(@"^[\w@%+=:,./-]+$", RegexOptions.Compiled | RegexOptions.ECMAScript);

    public static string Quote(string s)
    {
        if (s.Length == 0) return "''";
        if (Safe.IsMatch(s)) return s;
        return "'" + s.Replace("'", "'\"'\"'") + "'";
    }

    public static string Join(IEnumerable<string> argv) => string.Join(" ", argv.Select(Quote));

    /// <summary>
    /// Python's <c>shlex.split</c> (POSIX mode), enough for a <c>launch</c> command line.
    /// An unfinished quote or a trailing backslash is a <c>ValueError</c> there, with these
    /// exact messages; a half-parsed command must not run as if it were whole.
    /// </summary>
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
                else if (c == '\\')
                {
                    if (i + 1 >= command.Length) throw new ArgumentException("No escaped character");
                    // shlex only unescapes \" and \\ inside double quotes; \$ keeps its backslash.
                    if ("\"\\".Contains(command[i + 1])) cur.Append(command[++i]);
                    else cur.Append(c);
                }
                else cur.Append(c);
                continue;
            }
            if (char.IsWhiteSpace(c)) { if (inWord) { result.Add(cur.ToString()); cur.Clear(); inWord = false; } continue; }
            inWord = true;
            if (c == '\'') inSingle = true;
            else if (c == '"') inDouble = true;
            else if (c == '\\')
            {
                if (i + 1 >= command.Length) throw new ArgumentException("No escaped character");
                cur.Append(command[++i]);
            }
            else cur.Append(c);
        }
        if (inSingle || inDouble) throw new ArgumentException("No closing quotation");
        if (inWord) result.Add(cur.ToString());
        return result;
    }
}
