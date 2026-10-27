namespace Tramoya.Core;

/// <summary>One piece of a deck's final timeline: a still slide or the clip.</summary>
/// <param name="Kind"><c>"slide"</c> or <c>"clip"</c>.</param>
// TODO(integration): implement ISegment once Captions.cs lands.
public sealed record Segment(string Kind, string Name, string Source, double Duration, string? Voice = null);

/// <summary>
/// Scene and deck assembly (port of <c>tramoya/assembly.py</c>): turn marks or a deck plan
/// into rendered clips. Two pipelines live here, both driven through a <see cref="Runner"/>
/// so they can be exercised without ever shelling out:
/// <list type="bullet">
/// <item><see cref="AssembleMarks"/>: a screen recording plus <c>scene:*</c>/<c>wait:*</c>
/// marks becomes one clip per scene, waits longer than <see cref="WaitThreshold"/> sped up
/// with a caption, each fitted to its own narration clip (<see cref="BuildSceneClip"/>,
/// <see cref="FitAndMux"/>), then concatenated.</item>
/// <item><see cref="AssembleDeck"/>: a slide deck read aloud, with a recorded demo clip
/// spliced in right after the page that announces it (<see cref="PlanSegments"/>,
/// <see cref="RenderSlide"/>, <see cref="RenderClip"/>).</item>
/// </list>
/// Every argv is byte-for-byte what the Python code emits; the tests compare them literally.
/// </summary>
public static class Assembly
{
    /// <summary>Seconds; below this, a wait window is left alone.</summary>
    public const double WaitThreshold = 20.0;
    /// <summary>Seconds; a long wait is sped up to land around this length.</summary>
    public const double WaitTarget = 4.0;
    /// <summary>A scene shorter than this (a fast rehearsal) is clamped to one frame.</summary>
    public const double MinScene = 0.05;
    /// <summary>Seconds of quiet after a narration, so pages don't clip into each other.</summary>
    public const double PageTail = 0.6;
    /// <summary>A page with no voice clip (a section divider) still needs to be read.</summary>
    public const double SilentPage = 2.2;

    private const int Fps = 30;
    private const int GridWidth = 1920;
    private const int GridHeight = 1080;

    /// <summary>A short caption for a sped-up wait, e.g. "4 minutes later".</summary>
    public static string HumanizeWait(double seconds, string lang)
    {
        // Python's round() is banker's rounding.
        var minutes = (int)Math.Round(seconds / 60.0, MidpointRounding.ToEven);
        if (minutes <= 0)
            return lang == "es" ? "unos segundos despues" : "a few seconds later";
        if (lang == "es")
            return $"{minutes} {(minutes == 1 ? "minuto" : "minutos")} despues";
        return $"{minutes} {(minutes == 1 ? "minute" : "minutes")} later";
    }

    /// <summary>
    /// Deck order in, timeline out. The clip does not replace <paramref name="clipPage"/>:
    /// that page is read first (it is what announces the recording), and the clip plays
    /// right after it.
    /// </summary>
    public static List<Segment> PlanSegments(
        IReadOnlyDictionary<string, string> pages,
        IReadOnlyList<string> order,
        IReadOnlyDictionary<string, double> durations,
        string slidesDir,
        string? clip,
        double clipDuration,
        IReadOnlyDictionary<string, string>? voices = null,
        string clipPage = "video")
    {
        var plan = new List<Segment>();
        foreach (var name in order)
        {
            var page = pages[name];
            var duration = durations.TryGetValue(name, out var spoken) ? spoken + PageTail : SilentPage;
            string? voice = null;
            voices?.TryGetValue(name, out voice);
            plan.Add(new Segment("slide", name, Path.Combine(slidesDir, page + ".png"), duration, voice));
            if (name == clipPage && clip is not null)
                plan.Add(new Segment("clip", "demo", clip, clipDuration));
        }
        return plan;
    }

    private static readonly string[] CutEncode = { "-an", "-c:v", "libx264", "-preset", "fast", "-crf", "20" };

    /// <summary>A plain video-only cut of <c>[from, to)</c>; timestamps are Python's <c>str(float)</c>.</summary>
    private static string[] Cut(double from, double to, string video, string outPath) =>
        new[] { "ffmpeg", "-loglevel", "error", "-y", "-ss", Fmt.Py(from), "-to", Fmt.Py(to), "-i", video }
            .Concat(CutEncode).Append(outPath).ToArray();

    /// <summary>
    /// Cut <c>[start, end)</c> out of <paramref name="video"/>, compressing any wait window
    /// inside it that is longer than <see cref="WaitThreshold"/> (with a caption), and
    /// return the path to the resulting video-only clip.
    /// </summary>
    public static string BuildSceneClip(
        Runner runner,
        string video,
        string workDir,
        string name,
        double start,
        double end,
        IReadOnlyList<(double Start, double End)> waits,
        string lang)
    {
        var localWaits = waits
            .Where(w => w.Start < end && w.End > start)
            .Select(w => (Start: Math.Max(0.0, w.Start - start), End: Math.Min(end - start, w.End - start)))
            .Where(w => w.End - w.Start > WaitThreshold)
            .ToList();

        if (end - start < MinScene)
            end = start + MinScene;

        var sceneDur = end - start;
        var outPath = Path.Combine(workDir, $"scene-{name}-video.mp4");

        if (localWaits.Count == 0)
        {
            runner.Run(Cut(start, end, video, outPath));
            return outPath;
        }

        var pieces = new List<string>();
        var cursor = 0.0;
        for (var i = 0; i < localWaits.Count; i++)
        {
            var (a, b) = localWaits[i];
            if (a > cursor)
            {
                var pre = Path.Combine(workDir, $"scene-{name}-pre{i}.mp4");
                runner.Run(Cut(start + cursor, start + a, video, pre));
                pieces.Add(pre);
            }
            var waitDur = b - a;
            var factor = Math.Max(1.0, waitDur / WaitTarget);
            var label = HumanizeWait(waitDur, lang);
            var fast = Path.Combine(workDir, $"scene-{name}-wait{i}.mp4");
            runner.Run(new[]
            {
                "ffmpeg", "-loglevel", "error", "-y",
                "-ss", Fmt.Py(start + a), "-to", Fmt.Py(start + b), "-i", video,
                "-an", "-vf", Ffmpeg.WaitCompress(factor, label),
                "-c:v", "libx264", "-preset", "fast", "-crf", "20", fast,
            });
            pieces.Add(fast);
            cursor = b;
        }
        if (cursor < sceneDur)
        {
            var post = Path.Combine(workDir, $"scene-{name}-post.mp4");
            runner.Run(Cut(start + cursor, end, video, post));
            pieces.Add(post);
        }

        if (pieces.Count == 1)
            return pieces[0];

        var listPath = Path.Combine(workDir, $"scene-{name}-concat.txt");
        Ffmpeg.WriteConcatList(pieces, listPath);
        runner.Run(Ffmpeg.ConcatArgv(listPath, outPath));
        return outPath;
    }

    /// <summary>
    /// Pad the video (freeze last frame) and/or the audio (silence) so both reach the same
    /// final duration, then mux them into one clip.
    /// </summary>
    public static (string Path, double Duration) FitAndMux(
        Runner runner, string workDir, string name, string videoClip, string? wavPath)
    {
        var videoDur = runner.ProbeDuration(videoClip);
        var wavDur = wavPath is not null ? runner.ProbeDuration(wavPath) : 0.0;
        var finalDur = Math.Max(videoDur, wavDur);
        var outPath = Path.Combine(workDir, $"scene-{name}-final.mp4");

        string videoSource;
        if (finalDur > videoDur + 0.02)
        {
            var padded = Path.Combine(workDir, $"scene-{name}-video-padded.mp4");
            runner.Run(new[]
            {
                "ffmpeg", "-loglevel", "error", "-y", "-i", videoClip,
                "-vf", Ffmpeg.FreezeTail(finalDur - videoDur),
                "-c:v", "libx264", "-preset", "fast", "-crf", "20", padded,
            });
            videoSource = padded;
        }
        else
        {
            videoSource = videoClip;
        }

        string[] cmd;
        if (wavPath is not null)
        {
            cmd = new[]
            {
                "ffmpeg", "-loglevel", "error", "-y",
                "-i", videoSource, "-i", wavPath,
                "-filter_complex",
                $"[1:a]aformat=sample_rates=48000:channel_layouts=mono,{Ffmpeg.Loudnorm}," +
                $"apad=whole_dur={Fmt.F(finalDur, 3)}[aout]",
                "-map", "0:v", "-map", "[aout]",
                "-t", Fmt.F(finalDur, 3),
                "-c:v", "libx264", "-preset", "fast", "-crf", "20",
                "-c:a", "aac", "-b:a", "128k",
                outPath,
            };
        }
        else
        {
            cmd = new[] { "ffmpeg", "-loglevel", "error", "-y", "-i", videoSource }
                .Concat(Ffmpeg.SilentAudio("mono", 48000))
                .Concat(new[]
                {
                    "-map", "0:v", "-map", "1:a",
                    "-t", Fmt.F(finalDur, 3),
                    "-c:v", "libx264", "-preset", "fast", "-crf", "20",
                    "-c:a", "aac", "-b:a", "128k",
                    outPath,
                }).ToArray();
        }
        runner.Run(cmd);
        return (outPath, finalDur);
    }

    private static string DefaultVoiceName(string name) => $"scene-{name}.wav";

    /// <summary>
    /// Marks + a screen recording in, one scene per clip, concatenated to <paramref name="outPath"/>.
    /// A missing voice clip is tolerated: that scene keeps its own video length and gets a
    /// silent audio track instead of failing the whole run. Both the canonical
    /// <c>voiceName(name)</c> filename and the legacy <c>escena-&lt;name&gt;.wav</c> one are
    /// tried before giving up.
    /// </summary>
    public static List<(string Name, double Raw, double Final)> AssembleMarks(
        Runner runner,
        string video,
        string marksPath,
        string voiceDir,
        string outPath,
        double offset = 0.0,
        string lang = "en",
        Func<string, string>? voiceName = null)
    {
        voiceName ??= DefaultVoiceName;
        var marks = MarkKeys.LoadMarks(marksPath, offset);
        var scenes = MarkKeys.SceneBounds(marks);
        if (scenes.Count == 0)
            throw new InvalidOperationException($"no scene marks found in {marksPath}");
        var waits = MarkKeys.WaitWindows(marks);

        var table = new List<(string Name, double Raw, double Final)>();
        var workDir = Path.Combine(Path.GetTempPath(), "tramoya-assemble-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var finalClips = new List<string>();
            foreach (var (name, (start, end)) in scenes)
            {
                var rawDur = end - start;
                var videoClip = BuildSceneClip(runner, video, workDir, name, start, end, waits, lang);

                var wavPath = Path.Combine(voiceDir, voiceName(name));
                if (!File.Exists(wavPath))
                {
                    var legacy = Path.Combine(voiceDir, $"escena-{name}.wav");
                    if (File.Exists(legacy)) wavPath = legacy;
                }

                var (finalClip, finalDur) = FitAndMux(
                    runner, workDir, name, videoClip, File.Exists(wavPath) ? wavPath : null);
                finalClips.Add(finalClip);
                table.Add((name, rawDur, finalDur));
            }

            var listPath = Path.Combine(workDir, "final-concat.txt");
            Ffmpeg.WriteConcatList(finalClips, listPath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            runner.Run(Ffmpeg.ConcatArgv(listPath, outPath));
        }
        finally
        {
            // A clip still held open by a dying ffmpeg must not turn a finished render into
            // an error: a stray temp directory is the lesser evil.
            try { Directory.Delete(workDir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        return table;
    }

    private static readonly string[] GridEncode =
    {
        "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-r", "30",
        "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
    };

    /// <summary>
    /// A still page with its own narration (or silence) already muxed in, so the concat
    /// step only ever joins uniform pieces.
    /// </summary>
    public static void RenderSlide(Runner runner, Segment seg, string outPath)
    {
        var cmd = new List<string>
        {
            "ffmpeg", "-y", "-loglevel", "error",
            "-loop", "1", "-framerate", Fps.ToString(), "-t", Fmt.F(seg.Duration, 3), "-i", seg.Source,
        };
        if (seg.Voice is not null)
        {
            cmd.AddRange(new[]
            {
                "-i", seg.Voice,
                "-filter_complex", $"[1:a]{Ffmpeg.Loudnorm},apad[a]",
                "-map", "0:v", "-map", "[a]",
            });
        }
        else
        {
            cmd.AddRange(Ffmpeg.SilentAudio("stereo", 48000));
            cmd.AddRange(new[] { "-map", "0:v", "-map", "1:a" });
        }
        cmd.AddRange(new[] { "-t", Fmt.F(seg.Duration, 3), "-vf", Ffmpeg.PadToGrid(GridWidth, GridHeight) });
        cmd.AddRange(GridEncode);
        cmd.Add(outPath);
        runner.Run(cmd);
    }

    /// <summary>
    /// The demo clip is re-encoded to the same grid as the slides; without this the concat
    /// demuxer stitches mismatched streams and the audio drifts.
    /// </summary>
    public static void RenderClip(Runner runner, Segment seg, string outPath)
    {
        var cmd = new List<string>
        {
            "ffmpeg", "-y", "-loglevel", "error", "-i", seg.Source,
            "-vf", Ffmpeg.PadToGrid(GridWidth, GridHeight),
        };
        cmd.AddRange(GridEncode);
        cmd.Add(outPath);
        runner.Run(cmd);
    }

    /// <summary>Render every segment of <paramref name="plan"/> and concatenate them into <paramref name="outPath"/>.</summary>
    public static List<string> AssembleDeck(Runner runner, IReadOnlyList<Segment> plan, string workDir, string outPath)
    {
        var parts = new List<string>();
        for (var i = 0; i < plan.Count; i++)
        {
            var seg = plan[i];
            var part = Path.Combine(workDir, i.ToString("000") + ".mp4");
            if (seg.Kind == "clip") RenderClip(runner, seg, part);
            else RenderSlide(runner, seg, part);
            parts.Add(part);
        }

        var listPath = Path.Combine(workDir, "concat.txt");
        Ffmpeg.WriteConcatList(parts, listPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        runner.Run(Ffmpeg.ConcatArgv(listPath, outPath));
        return parts;
    }
}
