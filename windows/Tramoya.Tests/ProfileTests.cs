using Tramoya.Core;

namespace Tramoya.Tests;

/// <summary>
/// The app profile: one <c>tramoya.toml</c> that names the app, how to reach it, the
/// languages, and the scenes as plain step lines. Reading, writing and the step language
/// are pure; running steps is exercised on a fake stage (port of <c>tests/test_profile.py</c>).
/// </summary>
public class ProfileTests
{
    private const string Sample = """
        [app]
        name = "Notes"
        url = "http://127.0.0.1:8765/index.html"
        launch = "python -m http.server 8765"
        viewport = [1280, 720]

        [demo]
        languages = ["en", "es"]
        pace = 1.5

        [[scene]]
        name = "welcome"
        label = "A small notes app"
        steps = ["point h1", "say 6", "beat 1"]

        [scene.narration]
        en = "This is a small notes app."
        es = "Esta es una pequeña app de notas."

        [[scene]]
        name = "search"
        label = "Search filters as you type"
        steps = ["type #search | re", "say 5"]

        [scene.narration]
        en = "Typing filters the list."

        """;

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tramoya-profile-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    [Fact]
    public void LoadProfileReadsAppDemoAndScenes()
    {
        using var tmp = new TempDir();
        var path = tmp.File("tramoya.toml");
        File.WriteAllText(path, Sample);
        var profile = ProfileFile.Load(path);
        Assert.Equal("Notes", profile.Name);
        Assert.Equal("http://127.0.0.1:8765/index.html", profile.Url);
        Assert.Equal("python -m http.server 8765", profile.Launch);
        Assert.Equal((1280, 720), profile.Viewport);
        Assert.Equal(new[] { "en", "es" }, profile.Languages);
        Assert.Equal(1.5, profile.Pace);
        Assert.Equal(new[] { "welcome", "search" }, profile.Scenes.Select(s => s.Name));
        Assert.StartsWith("Esta", profile.Scenes[0].Narration["es"]);
        Assert.Equal(new[] { "type #search | re", "say 5" }, profile.Scenes[1].Steps);
        Assert.Equal(tmp.Path, profile.Root);
    }

    [Fact]
    public void LoadProfileAcceptsAnInlineArrayOfSceneTables()
    {
        // tomllib hands `scene = [{...}]` and `[[scene]]` over as the same list of dicts;
        // Tomlyn gives them different types and the port must read both.
        // An inline array is a top-level key, so it goes before the first [table] header.
        const string inline = """
            scene = [
                { name = "intro", steps = ["say 2", "beat 1"], narration = { en = "Hi there." } },
                { name = "outro", label = "The end", steps = ["beat 1"] },
            ]

            [app]
            name = "Notes"
            url = "http://127.0.0.1:8765/index.html"

            [demo]
            languages = ["en"]
            """;
        using var tmp = new TempDir();
        var path = tmp.File("tramoya.toml");
        File.WriteAllText(path, inline);
        var profile = ProfileFile.Load(path);
        Assert.Equal(new[] { "intro", "outro" }, profile.Scenes.Select(s => s.Name));
        Assert.Equal("intro", profile.Scenes[0].Label);
        Assert.Equal("The end", profile.Scenes[1].Label);
        Assert.Equal(new[] { "say 2", "beat 1" }, profile.Scenes[0].Steps);
        Assert.Equal(new Dictionary<string, string> { ["en"] = "Hi there." }, profile.Scenes[0].Narration);
        Assert.Empty(profile.Scenes[1].Narration);
    }

    [Fact]
    public void LoadProfileNeedsAUrlOrALaunch()
    {
        using var tmp = new TempDir();
        var path = tmp.File("tramoya.toml");
        File.WriteAllText(path, "[app]\nname = \"X\"\n");
        var exc = Assert.Throws<ArgumentException>(() => ProfileFile.Load(path));
        Assert.Contains("url", exc.Message);
    }

    [Fact]
    public void DumpThenLoadRoundTrips()
    {
        using var tmp = new TempDir();
        var profile = new Profile("Quotes \"R\" Us", url: "http://localhost:3000/")
        {
            Languages = new List<string> { "en" },
            Scenes = new List<Scene>
            {
                new("one", "First", new Dictionary<string, string> { ["en"] = "Hello, \"world\"." },
                    new List<string> { "click #go", "say 3" }),
            },
        };
        var path = tmp.File("tramoya.toml");
        File.WriteAllText(path, ProfileFile.Dump(profile));
        var again = ProfileFile.Load(path);
        Assert.Equal(profile.Name, again.Name);
        Assert.Equal(new Dictionary<string, string> { ["en"] = "Hello, \"world\"." }, again.Scenes[0].Narration);
        Assert.Equal(new[] { "click #go", "say 3" }, again.Scenes[0].Steps);
    }

    [Fact]
    public void DumpProfileWritesTheSameTextAsThePythonVersion()
    {
        // Both implementations must write byte-identical files; this is the Python output.
        var profile = new Profile("Notes", url: "http://127.0.0.1:8765/index.html", launch: "python -m http.server 8765")
        {
            Languages = new List<string> { "en", "es" },
            Pace = 1.5,
            Scenes = new List<Scene>
            {
                new("welcome", "A small notes app",
                    new Dictionary<string, string> { ["en"] = "This is a small notes app.", ["es"] = "Esta es una pequeña app de notas." },
                    new List<string> { "point h1", "say 6", "beat 1" }),
                new("search", "Search filters as you type",
                    new Dictionary<string, string> { ["en"] = "Typing filters the list." },
                    new List<string> { "type #search | re", "say 5" }),
            },
        };
        Assert.Equal(Sample.ReplaceLineEndings("\n"), ProfileFile.Dump(profile));
        Assert.Contains("pace = 1.0\n", ProfileFile.Dump(new Profile("N", url: "http://x/")));
    }

    [Fact]
    public void ScriptIsOneKeyPerSceneWithTheScenePrefix()
    {
        var profile = new Profile("N", url: "http://x/")
        {
            Scenes = new List<Scene>
            {
                new("a", "A", new Dictionary<string, string> { ["en"] = "one" }, new List<string>()),
                new("b", "B", new Dictionary<string, string> { ["en"] = "two", ["es"] = "dos" }, new List<string>()),
            },
        };
        var script = profile.Script();
        Assert.Equal(new[] { "scene-a", "scene-b" }, script.Keys);
        Assert.Equal(new Dictionary<string, string> { ["en"] = "one" }, script["scene-a"]);
        Assert.Equal(new Dictionary<string, string> { ["en"] = "two", ["es"] = "dos" }, script["scene-b"]);
    }

    [Fact]
    public void ScriptKeepsTheLastSceneWhenNamesRepeat()
    {
        // A dict comprehension in Python: the later scene overwrites the earlier one and
        // the key stays where it first appeared.
        var profile = new Profile("N", url: "http://x/")
        {
            Scenes = new List<Scene>
            {
                new("a", "A", new Dictionary<string, string> { ["en"] = "first" }, new List<string>()),
                new("b", "B", new Dictionary<string, string> { ["en"] = "middle" }, new List<string>()),
                new("a", "A again", new Dictionary<string, string> { ["en"] = "last" }, new List<string>()),
            },
        };
        var script = profile.Script();
        Assert.Equal(new[] { "scene-a", "scene-b" }, script.Keys);
        Assert.Equal("last", script["scene-a"]["en"]);
    }

    [Theory]
    [InlineData("click #save", "click", new[] { "#save" })]
    [InlineData("click_text New note", "click_text", new[] { "New note" })]
    [InlineData("type #title | Water the plants", "type", new[] { "#title", "Water the plants" })]
    [InlineData("select #tag | home", "select", new[] { "#tag", "home" })]
    [InlineData("point h1", "point", new[] { "h1" })]
    [InlineData("beat 1.5", "beat", new[] { "1.5" })]
    [InlineData("say 6", "say", new[] { "6" })]
    [InlineData("say", "say", new string[0])]
    [InlineData("scroll 300", "scroll", new[] { "300" })]
    [InlineData("press Enter", "press", new[] { "Enter" })]
    [InlineData("clear #search", "clear", new[] { "#search" })]
    [InlineData("goto /settings", "goto", new[] { "/settings" })]
    public void ParseStepSplitsVerbAndArguments(string line, string verb, string[] args)
    {
        var step = Steps.Parse(line);
        Assert.Equal(verb, step.Verb);
        Assert.Equal(args, step.Args);
    }

    [Fact]
    public void ParseStepRejectsUnknownVerbsAndBadArity()
    {
        var unknown = Assert.Throws<StepError>(() => Steps.Parse("dance #floor"));
        Assert.Contains("unknown", unknown.Message);
        Assert.Equal("unknown step 'dance' in 'dance #floor'; known: beat, clear, click, click_text, goto, point, press, say, scroll, select, type, wait", unknown.Message);
        var arity = Assert.Throws<StepError>(() => Steps.Parse("type #title"));
        Assert.Contains("type", arity.Message);
        Assert.Equal("type takes type <selector> | <text>, got 'type #title'", arity.Message);
    }

    /// <summary>Records every call as the Python double does; <c>clear</c> is one call here because <c>IStage</c> owns the fill + input event.</summary>
    private sealed class FakeStage : IStage
    {
        public List<string> Calls { get; } = new();
        public Task PointAtAsync(string s) { Calls.Add($"point {s}"); return Task.CompletedTask; }
        public Task ClickAsync(string s) { Calls.Add($"click {s}"); return Task.CompletedTask; }
        public Task ClickTextAsync(string t) { Calls.Add($"click_text {t}"); return Task.CompletedTask; }
        public Task TypeIntoAsync(string s, string t) { Calls.Add($"type {s} {t}"); return Task.CompletedTask; }
        public Task SelectAsync(string s, string v) { Calls.Add($"select {s} {v}"); return Task.CompletedTask; }
        public Task ClearAsync(string s) { Calls.Add($"clear {s}"); return Task.CompletedTask; }
        public Task ScrollAsync(int dy) { Calls.Add($"scroll {dy}"); return Task.CompletedTask; }
        public Task PressAsync(string k) { Calls.Add($"press {k}"); return Task.CompletedTask; }
        public Task GotoAsync(string u) { Calls.Add($"goto {u}"); return Task.CompletedTask; }
        public Task BeatAsync(double b) { Calls.Add($"beat {Fmt.Py(b)}"); return Task.CompletedTask; }
        public Task WaitForAsync(string s, double t) { Calls.Add($"wait {s} {Fmt.Py(t)}"); return Task.CompletedTask; }
    }

    [Fact]
    public async Task RunStepsWaitOpensAWaitWindowOnTheStage()
    {
        var stage = new FakeStage();
        await Steps.RunAsync(stage, new[] { "wait #listo | 180", "point #listo" }, say: _ => Task.CompletedTask);
        Assert.Equal(new[] { "wait #listo 180.0", "point #listo" }, stage.Calls);
        Assert.Throws<StepError>(() => Steps.Parse("wait #listo"));
    }

    [Fact]
    public async Task RunStepsDrivesTheStageInOrder()
    {
        var stage = new FakeStage();
        var said = new List<double>();
        await Steps.RunAsync(stage,
            new[] { "point h1", "type #q | re", "say 4", "clear #q", "press Enter", "beat 2", "goto /x" },
            say: fallback => { said.Add(fallback); return Task.CompletedTask; },
            baseUrl: "http://app/");
        Assert.Equal(new[]
        {
            "point h1", "type #q re", "clear #q", "press Enter", "beat 2.0", "goto http://app/x",
        }, stage.Calls);
        Assert.Equal(new[] { 4.0 }, said);
    }

    [Fact]
    public async Task RunStepsNamesTheFailingLine()
    {
        var exc = await Assert.ThrowsAsync<StepError>(() =>
            Steps.RunAsync(new FakeStage(), new[] { "point h1", "jump" }, say: _ => Task.CompletedTask));
        Assert.Contains("line 2", exc.Message);
        Assert.IsType<StepError>(exc.InnerException);
    }
}
