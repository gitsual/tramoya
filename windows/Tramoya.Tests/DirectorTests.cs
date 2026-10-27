using System.Globalization;
using Tramoya.Core;
using Tramoya.Stage;

namespace Tramoya.Tests;

/// <summary>
/// Port of <c>tests/test_director.py</c>: the generic director reads a profile, brings
/// the app up, runs every scene through the step language, writes the take, the marks,
/// the narration script and one accessibility snapshot per scene. The browser and the
/// app process are doubles, injected through <see cref="DirectorHooks"/> where the
/// Python tests monkeypatch module names.
/// </summary>
public sealed class DirectorTests
{
    private sealed class FakeStage : IDirectedStage
    {
        public List<string> Log { get; } = new();
        public string Tree { get; set; } = "- heading \"Notes\"\n- button \"New note\"";

        public Task PointAtAsync(string s) { Log.Add($"point {s}"); return Task.CompletedTask; }
        public Task ClickAsync(string s) { Log.Add($"click {s}"); return Task.CompletedTask; }
        public Task ClickTextAsync(string t) { Log.Add($"click_text {t}"); return Task.CompletedTask; }
        public Task TypeIntoAsync(string s, string t) { Log.Add($"type {s}"); return Task.CompletedTask; }
        public Task SelectAsync(string s, string v) { Log.Add($"select {s}"); return Task.CompletedTask; }
        public Task ClearAsync(string s) { Log.Add($"clear {s}"); return Task.CompletedTask; }
        public Task ScrollAsync(int dy) { Log.Add($"scroll {dy}"); return Task.CompletedTask; }
        public Task PressAsync(string key) { Log.Add($"press {key}"); return Task.CompletedTask; }
        public Task GotoAsync(string url) { Log.Add($"goto {url}"); return Task.CompletedTask; }
        public Task BeatAsync(double b) { Log.Add($"beat {b.ToString(CultureInfo.InvariantCulture)}"); return Task.CompletedTask; }
        public Task CaptionAsync(string t) { Log.Add($"caption '{t}'"); return Task.CompletedTask; }
        public Task<string> SnapshotAsync(string selector = "body") => Task.FromResult(Tree);
    }

    private sealed class FakeSession(FakeStage stage, string video) : IStageSession
    {
        public IDirectedStage Stage => stage;
        public Task<string?> VideoPathAsync() => Task.FromResult<string?>(video);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeProc : IChildProcess
    {
        private readonly List<bool> _stopped;
        public FakeProc(List<bool> stopped) => _stopped = stopped;
        public bool HasExited => false;
        public bool Interrupt() { _stopped.Add(true); return true; }
        public bool WaitForExit(int milliseconds) => true;
        public void Kill() { }
    }

    /// <summary>
    /// The <c>fake_browser</c> fixture: an <c>open_stage</c> double that yields a FakeStage
    /// whose video "file" exists, plus the step runner, narrator and snapshot writer that
    /// live in other Core files and are therefore doubles here too.
    /// </summary>
    private sealed class FakeBrowser
    {
        public string? Url;
        public string? VideoDir;
        public (int Width, int Height) Viewport;
        public bool Headless;
        public string? StorageState;
        public FakeStage? Stage;
        public DirectorHooks Hooks { get; }

        public FakeBrowser(string tmp)
        {
            var video = Path.Combine(tmp, "raw", "abc.webm");
            Hooks = new DirectorHooks
            {
                OpenStage = (url, pacing, marks, videoDir, viewport, headless, storageState) =>
                {
                    Url = url; VideoDir = videoDir; Viewport = viewport; Headless = headless; StorageState = storageState;
                    Directory.CreateDirectory(Path.GetDirectoryName(video)!);
                    File.WriteAllBytes(video, "webm"u8.ToArray());
                    Stage = new FakeStage();
                    return Task.FromResult<IStageSession>(new FakeSession(Stage, video));
                },
                // Nothing answers before the app is launched (the short pre-launch probe);
                // the app answers once it is being waited for.
                HttpReady = (url, timeout) => Task.FromResult(timeout > 1),
                RunSteps = RunSteps,
                WriteSnapshot = (dir, scene, text) =>
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, scene + ".txt"), text.EndsWith('\n') ? text : text + "\n");
                },
                Narrate = (voiceDir, pacing) => async (key, fallback) =>
                {
                    await pacing.SleepAsync(fallback);
                    return fallback;
                },
            };
        }

        /// <summary>Just the verbs these profiles use; the real interpreter is Steps.RunAsync.</summary>
        private static async Task RunSteps(IStage stage, IList<string> steps, Func<double, Task> say, string baseUrl)
        {
            foreach (var line in steps)
            {
                var parts = line.Split(' ', 2);
                var rest = parts.Length > 1 ? parts[1] : "";
                switch (parts[0])
                {
                    case "point": await stage.PointAtAsync(rest); break;
                    case "click": await stage.ClickAsync(rest); break;
                    case "type":
                        var ab = rest.Split('|', 2);
                        await stage.TypeIntoAsync(ab[0].Trim(), ab[1].Trim());
                        break;
                    case "beat": await stage.BeatAsync(double.Parse(rest, CultureInfo.InvariantCulture)); break;
                    case "say": await say(rest == "" ? 0.0 : double.Parse(rest, CultureInfo.InvariantCulture)); break;
                    default: throw new ArgumentException($"unknown verb in test profile: {line}");
                }
            }
        }
    }

    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "tramoya-director-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static Profile MakeProfile(string tmp, string? launch = null, string? ready = null, string? storageState = null) =>
        new("Notes", url: "http://127.0.0.1:1/index.html", launch: launch, root: tmp)
        {
            Ready = ready,
            StorageState = storageState,
            Languages = new() { "en", "es" },
            Scenes = new()
            {
                new Scene("welcome", "A small notes app", new() { ["en"] = "Hi", ["es"] = "Hola" },
                    new() { "point h1", "say 2", "beat 0.1" }),
                new Scene("search", "Search", new() { ["en"] = "Type" }, new() { "type #q | re", "say" }),
            },
        };

    [Fact]
    public async Task RunProfileWritesTakeMarksScriptAndSnapshots()
    {
        var tmp = TempDir();
        var browser = new FakeBrowser(tmp);
        var @out = Path.Combine(tmp, "out");
        var result = await Director.RunProfileAsync(MakeProfile(tmp), @out, rehearse: true, hooks: browser.Hooks);

        Assert.Equal("webm"u8.ToArray(), File.ReadAllBytes(Path.Combine(@out, "take.webm")));
        var marks = Json.LoadNode(Path.Combine(@out, "marks.json"))!.AsArray();
        var keys = marks.Select(m => m!["key"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "scene:welcome:start", "scene:welcome:end", "scene:search:start", "scene:search:end" }, keys);
        var script = Json.Load<Dictionary<string, Dictionary<string, string>>>(Path.Combine(@out, "script.json"))!;
        Assert.Equal(new[] { "scene-welcome", "scene-search" }, script.Keys);
        Assert.Equal(new Dictionary<string, string> { ["en"] = "Hi", ["es"] = "Hola" }, script["scene-welcome"]);
        Assert.Equal(new Dictionary<string, string> { ["en"] = "Type" }, script["scene-search"]);
        var snapshots = Directory.GetFiles(Path.Combine(@out, "snapshots")).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(new[] { "search.txt", "welcome.txt" }, snapshots);
        Assert.Contains("New note", File.ReadAllText(Path.Combine(@out, "snapshots", "welcome.txt")));
        Assert.Equal(new[] { "welcome", "search" }, result.Scenes);
        Assert.Equal("http://127.0.0.1:1/index.html", browser.Url);
        Assert.Equal((1280, 720), browser.Viewport);
        var log = browser.Stage!.Log;
        Assert.True(log.IndexOf("caption 'A small notes app'") < log.IndexOf("point h1"));
    }

    [Fact]
    public async Task RunProfileLaunchesTheAppAndWaitsForIt()
    {
        var tmp = TempDir();
        var browser = new FakeBrowser(tmp);
        var started = new List<(string Cmd, string Cwd)>();
        var stopped = new List<bool>();
        browser.Hooks.LaunchApp = (cmd, cwd) =>
        {
            started.Add((cmd, cwd));
            return new FakeProc(stopped);
        };
        var profile = MakeProfile(tmp, launch: "python -m http.server 1", ready: "http://127.0.0.1:1/");
        await Director.RunProfileAsync(profile, Path.Combine(tmp, "out"), rehearse: true, hooks: browser.Hooks);
        Assert.Equal(new[] { ("python -m http.server 1", tmp) }, started);
        Assert.Equal(new[] { true }, stopped);
    }

    [Fact]
    public async Task RunProfileFailsClearlyWhenTheAppNeverAnswers()
    {
        var tmp = TempDir();
        var browser = new FakeBrowser(tmp);
        browser.Hooks.HttpReady = (url, timeout) => Task.FromResult(false);
        var exc = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Director.RunProfileAsync(MakeProfile(tmp), Path.Combine(tmp, "out"), rehearse: true, hooks: browser.Hooks));
        Assert.Contains("did not answer", exc.Message);
    }

    [Fact]
    public async Task RunProfileOverridesUrlAndStorageState()
    {
        var tmp = TempDir();
        var browser = new FakeBrowser(tmp);
        var profile = MakeProfile(tmp, storageState: "auth.json");
        File.WriteAllText(Path.Combine(tmp, "auth.json"), "{}");
        await Director.RunProfileAsync(profile, Path.Combine(tmp, "out"), rehearse: true, url: "http://other/", hooks: browser.Hooks);
        Assert.Equal("http://other/", browser.Url);
        Assert.Equal(Path.Combine(tmp, "auth.json"), browser.StorageState);
    }

    [Fact]
    public async Task RunProfileNeedsScenes()
    {
        var tmp = TempDir();
        var browser = new FakeBrowser(tmp);
        var profile = MakeProfile(tmp);
        profile.Scenes = new();
        var exc = await Assert.ThrowsAsync<ArgumentException>(
            () => Director.RunProfileAsync(profile, Path.Combine(tmp, "out"), rehearse: true, hooks: browser.Hooks));
        Assert.Contains("no scenes", exc.Message);
    }

    [Fact]
    public void PlanListsScenesAndStepsWithoutABrowser()
    {
        var text = Director.Describe(MakeProfile(TempDir()));
        Assert.Contains("welcome", text);
        Assert.Contains("point h1", text);
        Assert.Contains("2 scenes", text);
    }

    // --- snapshots ------------------------------------------------------------

    [Fact(Skip = "Snapshots.WriteSnapshot/DiffSnapshots are Tramoya.Core/Snapshots.cs, ported separately; see SnapshotsTests")]
    public void SnapshotDiffReportsChangedMissingAndNewScenes()
    {
    }

    /// <summary>
    /// A stale server (or someone else's) on the same port would hand the demo the wrong
    /// page; better to stop before launching anything.
    /// </summary>
    [Fact]
    public async Task RunProfileRefusesToLaunchWhenSomethingAlreadyAnswers()
    {
        var tmp = TempDir();
        var browser = new FakeBrowser(tmp);
        var started = new List<string>();
        browser.Hooks.LaunchApp = (cmd, cwd) =>
        {
            started.Add(cmd);
            return new FakeProc(new List<bool>());
        };
        browser.Hooks.HttpReady = (url, timeout) => Task.FromResult(true);
        var profile = MakeProfile(tmp, launch: "python -m http.server 1", ready: "http://127.0.0.1:1/");
        var exc = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Director.RunProfileAsync(profile, Path.Combine(tmp, "out"), rehearse: true, hooks: browser.Hooks));
        Assert.Contains("already answers", exc.Message);
        Assert.Empty(started);
    }
}
