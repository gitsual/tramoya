using Tramoya.Core;

namespace Tramoya.Tests;

/// <summary>
/// <c>tramoya init</c>: a linear questionnaire, every question mirrored by a flag, answered
/// here by a scripted double so no terminal is involved (port of <c>tests/test_wizard.py</c>).
/// </summary>
public class WizardTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tramoya-wizard-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    [Fact]
    public void InterviewBuildsAProfileFromAnswers()
    {
        var answers = new ScriptedAnswers(new[]
        {
            "Notes",                                  // name
            "url",                                    // reach: url | launch
            "http://127.0.0.1:8000/",                 // url
            "n",                                      // login?
            "en, es",                                 // languages
            "welcome: A small notes app",             // scene 1
            "search: Search filters as you type",     // scene 2
            "",                                       // end of scenes
        });
        var profile = Wizard.Interview(answers);
        Assert.Equal("Notes", profile.Name);
        Assert.Equal("http://127.0.0.1:8000/", profile.Url);
        Assert.Null(profile.Launch);
        Assert.Equal(new[] { "en", "es" }, profile.Languages);
        Assert.Equal(new[] { ("welcome", "A small notes app"), ("search", "Search filters as you type") },
            profile.Scenes.Select(s => (s.Name, s.Label)));
        Assert.Equal(new[] { "say" }, profile.Scenes[0].Steps);
        Assert.Equal(new Dictionary<string, string> { ["en"] = "", ["es"] = "" }, profile.Scenes[0].Narration);
        Assert.True(answers.Exhausted);
    }

    [Fact]
    public void InterviewCanLaunchTheAppAndKeepASession()
    {
        var answers = new ScriptedAnswers(new[]
        {
            "Shop", "launch", "npm run dev", "http://localhost:5173/", "y", "en",
            "home: The storefront", "",
        });
        var profile = Wizard.Interview(answers);
        Assert.Equal("npm run dev", profile.Launch);
        Assert.Equal("http://localhost:5173/", profile.Url);
        Assert.Equal(".tramoya/auth.json", profile.StorageState);
    }

    [Fact]
    public void InterviewSlugsSceneNamesAndRejectsDuplicates()
    {
        var answers = new ScriptedAnswers(new[]
        {
            "N", "url", "http://x/", "n", "en",
            "New Note!: Create a note", "new-note: again", "views", "",
        });
        var profile = Wizard.Interview(answers);
        Assert.Equal(new[] { "new-note", "views" }, profile.Scenes.Select(s => s.Name));
        Assert.Equal("views", profile.Scenes[1].Label);
        Assert.Contains(answers.Warnings, w => w.Contains("already"));
        Assert.Equal("scene 'new-note' already exists; skipped", answers.Warnings[0]);
    }

    [Fact]
    public void FlagAnswersMirrorTheQuestionsAndNameWhatIsMissing()
    {
        var flags = new FlagAnswers(name: "Notes", url: "http://x/", languages: "en",
                                    scenes: new[] { "welcome: Hi", "search: Find" });
        var profile = Wizard.Interview(flags);
        Assert.Equal("Notes", profile.Name);
        Assert.Equal(2, profile.Scenes.Count);
        var noUrl = Assert.Throws<MissingAnswer>(() => Wizard.Interview(new FlagAnswers(name: "Notes")));
        Assert.Contains("--url or --launch", noUrl.Message);
        var noScene = Assert.Throws<MissingAnswer>(() => Wizard.Interview(new FlagAnswers(name: "Notes", url: "http://x/")));
        Assert.Contains("--scene", noScene.Message);
    }

    [Fact]
    public void ScaffoldWritesProfileScriptAndMakefile()
    {
        using var tmp = new TempDir();
        var profile = Wizard.Interview(new ScriptedAnswers(new[]
        {
            "Notes", "url", "http://x/", "n", "en, es", "welcome: Hi", "",
        }));
        var written = Wizard.Scaffold(profile, tmp.Path);
        var names = written.Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "Makefile", "script.json", "tramoya.toml" }, names);
        var again = ProfileFile.Load(tmp.File("tramoya.toml"));
        Assert.Equal("Notes", again.Name);
        Assert.Equal("welcome", again.Scenes[0].Name);
        var makefile = File.ReadAllText(tmp.File("Makefile"));
        Assert.Contains("tramoya direct tramoya.toml", makefile);
        Assert.Contains("tramoya tts --script", makefile);
        Assert.Contains("--lang es", makefile);
        Assert.Contains("\"scene-welcome\"", File.ReadAllText(tmp.File("script.json")));
    }

    [Fact]
    public void ScaffoldWritesTheMakefileThePythonVersionWrites()
    {
        // The exact text of `tramoya.wizard.MAKEFILE` after formatting: tabs in recipes,
        // one tts line per language, no blank line before `video:`.
        using var tmp = new TempDir();
        var profile = Wizard.Interview(new ScriptedAnswers(new[]
        {
            "My Notes", "url", "http://x/", "n", "en, es", "welcome: Hi", "",
        }));
        Wizard.Scaffold(profile, tmp.Path);
        var expected =
            "# Generated by `tramoya init`. Three targets, one video.\n"
            + "OUT ?= out\n"
            + "LANG ?= en\n"
            + "\n"
            + ".PHONY: take voices video all\n"
            + "all: video\n"
            + "\n"
            + "take:\n"
            + "\ttramoya direct tramoya.toml --out $(OUT)\n"
            + "\n"
            + "voices: take\n"
            + "\ttramoya tts --script $(OUT)/script.json --lang en --out-dir $(OUT)/voices/en\n"
            + "\ttramoya tts --script $(OUT)/script.json --lang es --out-dir $(OUT)/voices/es\n"
            + "video: voices\n"
            + "\ttramoya assemble --video $(OUT)/take.webm --marks $(OUT)/marks.json \\\n"
            + "\t  --voice-dir $(OUT)/voices/$(LANG) --out $(OUT)/my-notes-$(LANG).mp4 --lang $(LANG)\n";
        Assert.Equal(expected, File.ReadAllText(tmp.File("Makefile")));
        Assert.Equal("{\n  \"scene-welcome\": {\n    \"en\": \"\",\n    \"es\": \"\"\n  }\n}\n",
            File.ReadAllText(tmp.File("script.json")));
    }

    [Fact]
    public void ScaffoldRefusesToOverwriteWithoutForce()
    {
        using var tmp = new TempDir();
        var profile = Wizard.Interview(new ScriptedAnswers(new[] { "N", "url", "http://x/", "n", "en", "a: A", "" }));
        File.WriteAllText(tmp.File("tramoya.toml"), "old");
        var exc = Assert.Throws<IOException>(() => Wizard.Scaffold(profile, tmp.Path));
        Assert.Contains("--force", exc.Message);
        Wizard.Scaffold(profile, tmp.Path, force: true);
        Assert.DoesNotContain("old", File.ReadAllText(tmp.File("tramoya.toml")));
    }
}
