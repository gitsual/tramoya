using Tramoya.Core;

namespace Tramoya.Stage;

/// <summary>
/// The generic director (port of <c>tramoya/director.py</c>): <c>tramoya direct tramoya.toml</c>.
///
/// From a profile instead of hand-written code: bring the app up (or trust a URL), open
/// the stage, run every scene through the step language, and leave behind the take, the
/// marks, the narration script and one accessibility snapshot per scene.
///
/// Everything that touches a browser or a process is a module-level name in Python so
/// tests replace it with a double; here those names are the delegates of
/// <see cref="DirectorHooks"/>.
/// </summary>
public static class Director
{
    public const double ReadyTimeout = 30.0;

    /// <summary>Start the app's own command in its project directory.</summary>
    public static IChildProcess LaunchApp(string command, string cwd) => ChildProcess.Start(Shell.Split(command), cwd);

    private static void Stop(IChildProcess? proc)
    {
        if (proc is null || proc.HasExited) return;
        ChildProcess.Stop(proc, 10.0);
    }

    /// <summary>The plan a dry run prints: where the app is, then each scene's steps.</summary>
    public static string Describe(Profile profile)
    {
        var where = FirstNonEmpty(profile.Url, profile.Ready) ?? "?";
        var lines = new List<string> { $"{profile.Name}: {profile.Scenes.Count} scenes at {where}" };
        if (!string.IsNullOrEmpty(profile.Launch)) lines.Add($"  launch: {profile.Launch}");
        foreach (var scene in profile.Scenes)
        {
            lines.Add($"  {scene.Name}: {scene.Label}");
            lines.AddRange(scene.Steps.Select(step => $"      {step}"));
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Run every scene of <paramref name="profile"/> and write the take under <paramref name="out"/>.
    ///
    /// <paramref name="url"/>, <paramref name="launch"/> and <paramref name="storageState"/>
    /// override the profile's values, so a CI job can point the same scenes at a staging
    /// deployment. Credentials never come through here: <paramref name="storageState"/>
    /// is a Playwright session file.
    /// </summary>
    public static async Task<Result> RunProfileAsync(
        Profile profile,
        string @out,
        double? pace = null,
        bool rehearse = false,
        string? lang = null,
        string? url = null,
        string? launch = null,
        string? storageState = null,
        bool headless = true,
        DirectorHooks? hooks = null)
    {
        hooks ??= new DirectorHooks();
        if (profile.Scenes.Count == 0) throw new ArgumentException($"{profile.Name}: no scenes to run");
        @out = Path.GetFullPath(@out);
        var raw = Path.Combine(@out, "raw");
        if (Directory.Exists(raw)) Directory.Delete(raw, recursive: true);
        Directory.CreateDirectory(@out);

        launch ??= profile.Launch;
        var target = FirstNonEmpty(url, profile.Url, profile.Ready);
        var ready = FirstNonEmpty(profile.Ready, target);
        var stateFile = storageState ?? profile.StorageState;
        var statePath = string.IsNullOrEmpty(stateFile) ? null : Path.Combine(profile.Root, stateFile);
        if (statePath is not null && !File.Exists(statePath))
            throw new FileNotFoundException($"storage_state {statePath} does not exist; run `tramoya login` first", statePath);
        if (target is null) throw new ArgumentException($"{profile.Name}: no url or ready address to open");

        var pacing = new Pacing(pace: pace ?? profile.Pace, rehearse: rehearse, lang: lang ?? profile.Languages[0]);
        var runSteps = hooks.RunSteps ?? throw NotWired(nameof(DirectorHooks.RunSteps));
        var writeSnapshot = hooks.WriteSnapshot ?? throw NotWired(nameof(DirectorHooks.WriteSnapshot));
        var narrate = (hooks.Narrate ?? throw NotWired(nameof(DirectorHooks.Narrate)))(Path.Combine(@out, "voices"), pacing);
        var marks = new Marks(path: Path.Combine(@out, "marks.json"));
        Json.Write(Path.Combine(@out, "script.json"), profile.Script());

        var httpReady = hooks.HttpReady ?? ((u, t) => Browser.HttpReadyAsync(u, t));
        var openStage = hooks.OpenStage ?? DefaultOpenStage;
        var launchApp = hooks.LaunchApp ?? LaunchApp;
        var now = hooks.Now ?? Marks.Now;

        if (!string.IsNullOrEmpty(launch) && ready is not null && await httpReady(ready, 0.5).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"{ready} already answers before launching '{launch}'; something else is on that port " +
                "(a stale server?). Stop it or change the port in the profile");
        var proc = string.IsNullOrEmpty(launch) ? null : launchApp(launch, profile.Root);
        string? videoPath;
        Result result;
        try
        {
            if (ready is not null && !await httpReady(ready, ReadyTimeout).ConfigureAwait(false))
                throw new InvalidOperationException($"{profile.Name} did not answer at {ready} within {ReadyTimeout:0}s");
            result = new Result(@out);
            await using var session = await openStage(target, pacing, marks, raw, profile.Viewport, headless, statePath)
                .ConfigureAwait(false);
            var stage = session.Stage;
            marks.T0 = now();
            videoPath = await session.VideoPathAsync().ConfigureAwait(false);
            foreach (var scene in profile.Scenes)
            {
                var key = $"scene-{scene.Name}";
                Func<double, Task> say = fallback => narrate(key, fallback);

                using (marks.Scene(scene.Name, scene.Label))
                {
                    await stage.CaptionAsync(scene.Label).ConfigureAwait(false);
                    try
                    {
                        await runSteps(stage, scene.Steps, say, target).ConfigureAwait(false);
                        writeSnapshot(Path.Combine(@out, "snapshots"), scene.Name, await stage.SnapshotAsync().ConfigureAwait(false));
                    }
                    finally
                    {
                        await stage.CaptionAsync("").ConfigureAwait(false);
                    }
                }
                result.Scenes.Add(scene.Name);
            }
        }
        finally
        {
            Stop(proc);
        }

        if (videoPath is null) throw new InvalidOperationException($"{profile.Name}: the browser recorded no video");
        File.Copy(videoPath, result.Take, overwrite: true);
        return result;
    }

    private static async Task<IStageSession> DefaultOpenStage(
        string url, Pacing pacing, Marks marks, string? videoDir, (int Width, int Height) viewport, bool headless, string? storageState) =>
        await Browser.OpenStageAsync(url, pacing, marks, videoDir, viewport, headless, storageState).ConfigureAwait(false);

    private static InvalidOperationException NotWired(string hook) =>
        new($"DirectorHooks.{hook} is not set and has no default yet");

    /// <summary>Python's <c>a or b or c</c> over strings: empty counts as missing.</summary>
    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
}

/// <summary>Where a run left its files.</summary>
public sealed class Result
{
    public string Out { get; }
    public List<string> Scenes { get; } = new();

    public Result(string @out) => Out = @out;

    public string Take => Path.Combine(Out, "take.webm");
    public string Marks => Path.Combine(Out, "marks.json");
}

/// <summary>
/// The take is headless and silent: a scene only lasts as long as its clip would;
/// <c>assemble</c> adds the voice afterwards. Shaped like Core's <c>IClipPlayback</c>
/// (a <c>WaitAsync</c>) so the integrator can hand it to the <c>Narrator</c> as its player.
/// </summary>
public sealed class SilentClip
{
    public double Seconds { get; }
    public Pacing Pacing { get; }

    public SilentClip(double seconds, Pacing pacing)
    {
        Seconds = seconds;
        Pacing = pacing;
    }

    public async Task WaitAsync() => await Pacing.SleepAsync(Seconds).ConfigureAwait(false);
}

/// <summary>
/// The seams of <see cref="Director.RunProfileAsync"/>: what the Python tests monkeypatch
/// on the module (<c>open_stage</c>, <c>http_ready</c>, <c>launch_app</c>) plus the three
/// Core collaborators that live in other files. A null member means the default.
/// </summary>
public sealed class DirectorHooks
{
    public delegate Task<IStageSession> OpenStageFn(
        string url, Pacing pacing, Marks marks, string? videoDir, (int Width, int Height) viewport, bool headless, string? storageState);

    /// <summary>Default: <see cref="Browser.OpenStageAsync"/>.</summary>
    public OpenStageFn? OpenStage { get; set; }

    /// <summary>(url, timeout seconds) -> answers. Default: <see cref="Browser.HttpReadyAsync"/>.</summary>
    public Func<string, double, Task<bool>>? HttpReady { get; set; }

    /// <summary>(command, cwd) -> the running app. Default: <see cref="Director.LaunchApp"/>.</summary>
    public Func<string, string, IChildProcess>? LaunchApp { get; set; }

    /// <summary>(stage, steps, say, baseUrl): drive the stage through a scene's steps.</summary>
    // TODO(integration): default to Steps.RunAsync (Tramoya.Core/Steps.cs).
    public Func<IStage, IList<string>, Func<double, Task>, string, Task>? RunSteps { get; set; }

    /// <summary>
    /// (voiceDir, pacing) -> say(key, fallback seconds) -> seconds spoken. One narrator per
    /// run, as Python builds <c>Narrator(out / "voices", pacing, player=...)</c> once.
    /// </summary>
    // TODO(integration): default to a Narrator over voiceDir and pacing whose player is
    // clip => new SilentClip(Narration.WavSeconds(clip), pacing), returning narrator.SayAsync(key, fallback).
    public Func<string, Pacing, Func<string, double, Task<double>>>? Narrate { get; set; }

    /// <summary>(directory, scene, text): write the scene's accessibility snapshot.</summary>
    // TODO(integration): default to Snapshots.WriteSnapshot (Tramoya.Core/Snapshots.cs).
    public Action<string, string, string>? WriteSnapshot { get; set; }

    /// <summary>Unix seconds for the take's zero. Default: <see cref="Marks.Now"/>.</summary>
    public Func<double>? Now { get; set; }
}
