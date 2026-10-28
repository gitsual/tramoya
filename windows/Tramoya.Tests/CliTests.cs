using System.Text.Json.Nodes;
using Tramoya.App;
using Tramoya.Core;
using Tramoya.Stage;

namespace Tramoya.Tests;

/// <summary>
/// The `tramoya` command line: every subcommand that touches ffmpeg has a `--dry-run` that
/// prints the argv it would run and never encodes (port of <c>tests/test_cli.py</c>, plus the
/// four <c>test_cli_*</c> tests of <c>tests/test_assistant.py</c>). Output is captured by
/// swapping <see cref="Console.Out"/>; what the Python monkeypatches on the module is a
/// static delegate on <see cref="Cli"/> here.
/// </summary>
public class CliTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
    private static readonly string LegacyMarks = Path.Combine(Fixtures, "legacy-marks-v11.json");

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tramoya-cli-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    /// <summary>pytest's <c>capsys</c>: stdout and stderr into strings until disposed.</summary>
    private sealed class Capture : IDisposable
    {
        private readonly TextWriter _out = Console.Out;
        private readonly TextWriter _err = Console.Error;
        private StringWriter _stdout = new();
        private StringWriter _stderr = new();

        public Capture()
        {
            Console.SetOut(_stdout);
            Console.SetError(_stderr);
        }

        public string Out => _stdout.ToString();
        public string Err => _stderr.ToString();

        /// <summary><c>capsys.readouterr()</c>: what was printed so far, then start afresh.</summary>
        public (string Out, string Err) Read()
        {
            var pair = (Out, Err);
            _stdout = new StringWriter();
            _stderr = new StringWriter();
            Console.SetOut(_stdout);
            Console.SetError(_stderr);
            return pair;
        }

        public void Dispose()
        {
            Console.SetOut(_out);
            Console.SetError(_err);
        }
    }

    /// <summary><c>monkeypatch.chdir</c>: run in a folder, come back afterwards.</summary>
    private sealed class Chdir : IDisposable
    {
        private readonly string _previous = Directory.GetCurrentDirectory();
        public Chdir(string path) => Directory.SetCurrentDirectory(path);
        public void Dispose() => Directory.SetCurrentDirectory(_previous);
    }

    /// <summary><c>monkeypatch.setattr("builtins.input", ...)</c>: canned replies on stdin.</summary>
    private sealed class Stdin : IDisposable
    {
        private readonly TextReader _previous = Console.In;
        public Stdin(params string[] replies) => Console.SetIn(new StringReader(string.Join("\n", replies) + "\n"));
        public void Dispose() => Console.SetIn(_previous);
    }

    private static int Run(params string[] argv) => Cli.Run(argv);

    private static void WriteJson(string path, JsonNode node) => File.WriteAllText(path, node.ToJsonString());

    [Fact]
    public void HelpListsEverySubcommand()
    {
        using var cap = new Capture();
        Assert.Equal(0, Run("--help"));
        foreach (var name in new[] { "marks", "assemble", "deck", "captions", "music", "tts", "record", "cues", "agent" })
            Assert.Contains(name, cap.Out);
    }

    [Fact]
    public void AgentDryRunPrintsThePlanWithoutCheckingTheBrowser()
    {
        using var cap = new Capture();
        var browser = Cli.EnsureBrowser;
        Cli.EnsureBrowser = () => throw new Exception("Unexpected browser check");
        try
        {
            Assert.Equal(0, Run("agent", "Crear una nota", "--url", "https://example.test/",
                "--backend", "ollama", "--model", "modelo-local", "--max-pasos", "12", "--dry-run"));
        }
        finally { Cli.EnsureBrowser = browser; }
        Assert.Contains("backend: ollama", cap.Out);
        Assert.Contains("modelo: modelo-local", cap.Out);
        Assert.Contains("url: https://example.test/", cap.Out);
        Assert.Contains("objetivo: Crear una nota", cap.Out);
        Assert.Contains("máximo de pasos: 12", cap.Out);
    }

    [Fact]
    public void AgentWithoutUrlIsAUsageError()
    {
        using var cap = new Capture();
        Assert.Equal(2, Run("agent", "Crear una nota"));
        Assert.Contains("--url", cap.Err);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void AgentSavesTheProjectAndNarrationEvenWhenTheGoalIsNotReached(bool ok, int expected)
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var previousKit = Environment.GetEnvironmentVariable(Paths.KitVariable);
        var browser = Cli.EnsureBrowser;
        var run = Cli.RunAgent;
        Environment.SetEnvironmentVariable(Paths.KitVariable, tmp.Path);
        var checkedBrowser = false;
        Cli.EnsureBrowser = () => { checkedBrowser = true; return null; };
        Cli.RunAgent = (url, options, backend, model, ajustes, auth) =>
        {
            Assert.True(checkedBrowser);
            Assert.Equal("https://example.test/", url);
            Assert.Equal("Crear una nota", options.Goal);
            Assert.Equal("es", options.Lang);
            Assert.Equal(7, options.MaxPasos);
            Assert.True(options.Capturas);
            Assert.Equal("claude", backend);
            Assert.Equal("modelo", model);
            Assert.Equal("800p", ajustes["tamano"]!.GetValue<string>());
            File.WriteAllText(auth, "{\"cookies\":[],\"origins\":[]}");
            return Task.FromResult(new Tramoya.Agent.AgentResult
            {
                Ok = ok, Resumen = "Nota creada", Inicio = null, Titulo = "Notas",
                Avisos = ["Revisar la nota"],
                Pasos =
                [
                    new JsonObject { ["id"] = 42, ["tipo"] = "escribir", ["selector"] = "#nota",
                        ["valor"] = "Hola", ["texto"] = "Escribimos la nota." },
                    new JsonObject { ["id"] = 99, ["tipo"] = "clic", ["selector"] = "#guardar",
                        ["guarda"] = true, ["texto"] = "Guardamos la nota." },
                ],
            });
        };
        try
        {
            Assert.Equal(expected, Run("agent", "Crear una nota", "--url", "https://example.test/",
                "--nombre", "Mi nota", "--backend", "claude", "--model", "modelo",
                "--max-pasos", "7", "--capturas"));
            var opts = EasyHost.Options();
            var p = Tramoya.Easy.Proyectos.Leer(opts, "mi-nota");
            Assert.Equal("explicar", p["fase"]!.GetValue<string>());
            Assert.Equal("Mi nota", p["nombre"]!.GetValue<string>());
            Assert.Equal("https://example.test/", p["inicio"]!.GetValue<string>());
            Assert.Equal("Notas", p["titulo_web"]!.GetValue<string>());
            Assert.Equal("Nota creada", p["resumen"]!.GetValue<string>());
            Assert.Equal("Revisar la nota", p["avisos"]![0]!.GetValue<string>());
            var pasos = p["pasos"]!.AsArray();
            Assert.Equal(2, pasos.Count);
            Assert.Equal(1, pasos[0]!["id"]!.GetValue<int>());
            Assert.Equal(2, pasos[1]!["id"]!.GetValue<int>());
            Assert.Equal("Escribimos la nota.", pasos[0]!["texto"]!.GetValue<string>());
            Assert.Equal("Guardamos la nota.", pasos[1]!["texto"]!.GetValue<string>());
            Assert.True(pasos[1]!["seguro"]!.GetValue<bool>());
            Assert.True(File.Exists(Path.Combine(Tramoya.Easy.Proyectos.Carpeta(opts, "mi-nota"), "auth.json")));
            Assert.Contains("proyecto mi-nota en ", cap.Out);
            Assert.Contains(": 2 pasos", cap.Out);
            if (!ok) Assert.Contains("no ha alcanzado el objetivo", cap.Err);
        }
        finally
        {
            Cli.RunAgent = run;
            Cli.EnsureBrowser = browser;
            Environment.SetEnvironmentVariable(Paths.KitVariable, previousKit);
        }
    }

    [Fact]
    public void AgentVideoSavesPreferencesAndPrintsTheEnginePathsAndExitCode()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var previousKit = Environment.GetEnvironmentVariable(Paths.KitVariable);
        var browser = Cli.EnsureBrowser;
        var run = Cli.RunAgent;
        var makeVideo = Cli.MakeAgentVideo;
        Environment.SetEnvironmentVariable(Paths.KitVariable, tmp.Path);
        Cli.EnsureBrowser = () => null;
        Cli.RunAgent = (_, _, _, _, _, auth) =>
        {
            File.WriteAllText(auth, "{}");
            return Task.FromResult(new Tramoya.Agent.AgentResult { Ok = true });
        };
        var video = tmp.File("nota.mp4");
        var srt = tmp.File("nota.srt");
        Cli.MakeAgentVideo = (carpeta, voz) =>
        {
            Assert.True(voz);
            var p = Json.LoadNode(Path.Combine(carpeta, "proyecto.json"))!.AsObject();
            Assert.True(p["voz"]!.GetValue<bool>());
            Assert.Equal(Paths.MisVideos, p["mis_videos"]!.GetValue<string>());
            Assert.Equal("800p", p["ajustes"]!["tamano"]!.GetValue<string>());
            Tramoya.Easy.Engine.Emitir(("ok", true), ("video", video), ("srt", srt));
            return Task.FromResult(3);
        };
        try
        {
            Assert.Equal(3, Run("agent", "Nota", "--url", "https://example.test/", "--video", "--voz"));
            Assert.Contains($"v\u00eddeo: {video}", cap.Out);
            Assert.Contains($"subt\u00edtulos: {srt}", cap.Out);
            Assert.Contains("\"ok\":true", cap.Out);
            var p = Tramoya.Easy.Proyectos.Leer(EasyHost.Options(), "nota");
            Assert.Equal(video, p["video"]!.GetValue<string>());
            Assert.Equal(srt, p["srt"]!.GetValue<string>());
        }
        finally
        {
            Cli.MakeAgentVideo = makeVideo;
            Cli.RunAgent = run;
            Cli.EnsureBrowser = browser;
            Environment.SetEnvironmentVariable(Paths.KitVariable, previousKit);
        }
    }

    [Fact]
    public void AgentStopsWhenChromiumIsUnavailable()
    {
        using var cap = new Capture();
        var browser = Cli.EnsureBrowser;
        var run = Cli.RunAgent;
        Cli.EnsureBrowser = () => 2;
        Cli.RunAgent = (_, _, _, _, _, _) => throw new Exception("Unexpected run");
        try { Assert.Equal(2, Run("agent", "Crear nota", "--url", "https://example.test/")); }
        finally { Cli.EnsureBrowser = browser; Cli.RunAgent = run; }
    }

    [Fact]
    public void MarksPrintsASceneTable()
    {
        using var cap = new Capture();
        Assert.Equal(0, Run("marks", LegacyMarks));
        Assert.Contains("apertura", cap.Out);
        Assert.Contains("13 scenes", cap.Out);
    }

    [Fact]
    public void MarksOffsetShiftsEveryTimestamp()
    {
        using var cap = new Capture();
        Run("marks", LegacyMarks);
        var plain = cap.Read().Out;
        Run("marks", LegacyMarks, "--offset", "10");
        var shifted = cap.Read().Out;
        Assert.NotEqual(plain, shifted);
        Assert.Contains("14.31", shifted); // 4.31 + 10
    }

    [Fact]
    public void AssembleDryRunPrintsFfmpegCommandsWithoutEncoding()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var rc = Run("assemble", "--video", "take.mp4", "--marks", LegacyMarks,
                      "--voice-dir", tmp.Path, "--out", tmp.File("out.mp4"), "--dry-run");
        Assert.Equal(0, rc);
        var @out = cap.Out;
        Assert.True(@out.Split("ffmpeg").Length - 1 >= 13);
        Assert.Contains("concat", @out);
        Assert.False(File.Exists(tmp.File("out.mp4")));
    }

    [Fact]
    public void AssembleReadsTheOffsetSidecarNextToTheVideo()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var video = tmp.File("take.mp4");
        WriteJson(tmp.File("take.mp4.offset.json"),
                  new JsonObject { ["rec_start"] = 0.0, ["director_start"] = 2.5, ["offset"] = 2.5 });
        Run("assemble", "--video", video, "--marks", LegacyMarks,
             "--voice-dir", tmp.Path, "--out", tmp.File("out.mp4"), "--dry-run");
        Assert.Contains("-ss 6.81", cap.Out); // 4.31 + 2.5
    }

    [Fact]
    public void CaptionsBurnDryRun()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var rc = Run("captions", "burn", "--video", "in.mp4", "--subtitles", "cues.srt",
                      "--out", tmp.File("o.mp4"), "--dry-run");
        Assert.Equal(0, rc);
        Assert.Contains("subtitles=cues.srt", cap.Out);
    }

    [Fact]
    public void CaptionsBurnWithTrimPlansCutsAndConcat()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        Run("captions", "burn", "--video", "in.mp4", "--subtitles", "cues.srt",
             "--out", tmp.File("o.mp4"), "--trim", "0-120,600-780", "--dry-run");
        var @out = cap.Out;
        Assert.Equal(4, @out.Split("ffmpeg").Length - 1);
        Assert.Contains("-ss 0", @out);
        Assert.Contains("-to 120", @out);
        Assert.Contains("-f concat", @out);
    }

    [Fact]
    public void MusicBedWritesAWav()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        Assert.Equal(0, Run("music", "bed", "--out", tmp.File("bed.wav")));
        Assert.True(new FileInfo(tmp.File("bed.wav")).Length > 1000);
    }

    [Fact]
    public void MusicMixDryRunDucksByDefault()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        Run("music", "mix", "--video", "v.mp4", "--bed", "bed.wav", "--out", tmp.File("o.mp4"), "--dry-run");
        Assert.Contains("sidechaincompress", cap.Read().Out);
        Run("music", "mix", "--video", "v.mp4", "--bed", "bed.wav", "--out", tmp.File("o.mp4"), "--solo", "--dry-run");
        Assert.DoesNotContain("sidechaincompress", cap.Read().Out);
    }

    [Fact]
    public void CuesToMarksConvertsAVivacTake()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var cues = tmp.File("take.cues");
        File.WriteAllText(cues, "1.000|Hello\n5.500|World\n9.000|\n");
        var meta = tmp.File("take.meta");
        File.WriteAllText(meta, "resolution=1920x1080\nelapsed=9.0\n");
        var @out = tmp.File("marks.json");
        var rc = Run("cues", "marks", "--cues", cues, "--meta", meta, "--duration", "10.0", "--out", @out);
        Assert.Equal(0, rc);
        var rows = JsonNode.Parse(File.ReadAllText(@out))!.AsArray();
        Assert.Equal("scene:0:start", rows[0]!["key"]!.GetValue<string>());
        Assert.Equal(2.0, rows[0]!["t"]!.GetValue<double>(), 6); // 1.0 + (10.0 - 9.0)
    }

    [Fact]
    public void CuesToAssWritesASubtitleFile()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var cues = tmp.File("take.cues");
        File.WriteAllText(cues, "1.000|Hello\n5.500|World\n9.000|\n");
        var @out = tmp.File("take.ass");
        var rc = Run("cues", "ass", "--cues", cues, "--width", "1920", "--height", "1080", "--out", @out);
        Assert.Equal(0, rc);
        var text = File.ReadAllText(@out);
        Assert.Contains("[V4+ Styles]", text);
        Assert.Contains("Hello", text);
        Assert.Contains("World", text);
    }

    [Fact]
    public void RecordDryRunPrintsTheRecorderArgv()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var rc = Run("record", "--out", tmp.File("take.mp4"), "--geometry", "0,48 1920x1032", "--fps", "10", "--dry-run");
        Assert.Equal(0, rc);
        Assert.Contains("wf-recorder", cap.Out);
        Assert.Contains("-r 10", cap.Out);
    }

    [Fact(Skip = "the Python test patches the Kokoro extra; on Windows tts speaks through SAPI and has no optional extra")]
    public void TtsWithoutTheExtraFailsWithAClearMessage() { }

    private static string ScriptFile(TempDir tmp)
    {
        var script = tmp.File("script.json");
        WriteJson(script, new JsonObject
        {
            ["intro"] = new JsonObject { ["en"] = "Hello.", ["es"] = "Hola.", ["zz"] = "x" },
            ["end"] = new JsonObject { ["en"] = "Bye.", ["es"] = "Adiós.", ["zz"] = "y" },
        });
        return script;
    }

    [VoiceFact("en")]
    public void TtsWithLangEnWritesOneWavPerKeyWithAnEnglishVoice()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var outDir = tmp.File("voices");
        var rc = Run("tts", "--script", ScriptFile(tmp), "--lang", "en", "--out-dir", outDir);
        Assert.Equal(0, rc);
        foreach (var key in new[] { "intro", "end" })
        {
            var wav = Path.Combine(outDir, $"{key}.wav");
            Assert.True(new FileInfo(wav).Length > 44, wav);
            Assert.Contains(wav, cap.Out);
        }
    }

    [Fact]
    public void TtsWithoutAVoiceForTheLanguageExitsTwoAndListsTheInstalledOnes()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var outDir = tmp.File("voices");
        var rc = Run("tts", "--script", ScriptFile(tmp), "--lang", "zz", "--out-dir", outDir);
        Assert.Equal(2, rc);
        Assert.Contains("tts: no Windows voice installed for 'zz'; installed:", cap.Err);
        Assert.False(Directory.Exists(outDir));
    }

    [VoiceFact("es")]
    public void TtsNamesTheScenesWithoutTextInThatLanguage()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var script = tmp.File("script.json");
        WriteJson(script, new JsonObject
        {
            ["intro"] = new JsonObject { ["en"] = "Hello.", ["es"] = "Hola." },
            ["end"] = new JsonObject { ["en"] = "Bye." },
        });
        var outDir = tmp.File("voices");
        var rc = Run("tts", "--script", script, "--lang", "es", "--out-dir", outDir);
        Assert.Equal(2, rc);
        Assert.Contains("tts: the script has no 'es' text for end; languages in it: en, es", cap.Err);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void DeckDryRunPlansPagesAndClip()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var slides = tmp.File("slides");
        Directory.CreateDirectory(slides);
        foreach (var name in new[] { "cover", "video", "end" })
            File.WriteAllBytes(Path.Combine(slides, $"{name}.png"), "png"u8.ToArray());
        WriteJson(Path.Combine(slides, "index.json"),
                  new JsonObject { ["pages"] = new JsonObject { ["cover"] = "001", ["video"] = "002", ["end"] = "003" } });
        var voices = tmp.File("voices");
        Directory.CreateDirectory(voices);
        var rc = Run("deck", "--slides", slides, "--voice-dir", voices, "--clip", "clip.mp4", "--clip-duration", "12",
                      "--out", tmp.File("deck.mp4"), "--dry-run");
        Assert.Equal(0, rc);
        Assert.Contains("cover", cap.Out);
        Assert.Contains("clip", cap.Out);
        Assert.Contains("ffmpeg", cap.Out);
    }

    [Fact]
    public void DirectDryRunPrintsTheDirectorArgv()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var script = tmp.File("director.py");
        File.WriteAllText(script, "print('hi')\n");
        var rc = Run("direct", script, "--out", tmp.File("out"), "--pace", "0.8", "--rehearse", "--lang", "en", "--dry-run");
        Assert.Equal(0, rc);
        var @out = cap.Out;
        Assert.Contains(script, @out);
        // shlex.join quotes a path with backslashes, which every Windows temp path has.
        Assert.Contains($"--out {Shell.Quote(tmp.File("out"))} --pace 0.8 --rehearse --lang en", @out);
        Assert.Contains("python", @out.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0]);
    }

    [Fact(Skip = "Python director scripts are not run on Windows: direct <script.py> exits with 2")]
    public void DirectRunsTheDirectorWithTheCurrentPython() { }

    // --- the profile path: init, direct tramoya.toml, import, capture, login, snapshots -------

    [Fact]
    public void HelpListsTheProfileSubcommands()
    {
        using var cap = new Capture();
        Run("--help");
        foreach (var name in new[] { "init", "import", "capture", "login", "snapshots" })
            Assert.Contains(name, cap.Out);
    }

    [Fact]
    public void InitNonInteractiveWritesAProject()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var rc = Run("init", tmp.Path, "--no-interactive", "--name", "Notes", "--url", "http://x/", "--lang", "en,es",
                      "--scene", "welcome: Hi", "--scene", "search: Find");
        Assert.Equal(0, rc);
        Assert.True(File.Exists(tmp.File("tramoya.toml")));
        Assert.True(File.Exists(tmp.File("script.json")));
        Assert.True(File.Exists(tmp.File("Makefile")));
        Assert.Contains("tramoya direct tramoya.toml", cap.Out);
    }

    [Fact]
    public void InitNonInteractiveNamesTheMissingFlag()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var rc = Run("init", tmp.Path, "--no-interactive", "--name", "Notes", "--url", "http://x/");
        Assert.Equal(2, rc);
        Assert.Contains("--scene", cap.Err);
    }

    [Fact]
    public void InitInteractiveReadsAnswersFromStdin()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        using var stdin = new Stdin("Notes", "url", "http://x/", "n", "en", "welcome: Hi", "");
        var tty = Cli.StdinIsTty;
        Cli.StdinIsTty = () => true;
        try
        {
            Assert.Equal(0, Run("init", tmp.Path));
        }
        finally
        {
            Cli.StdinIsTty = tty;
        }
        Assert.Contains("welcome", File.ReadAllText(tmp.File("tramoya.toml")));
    }

    [Fact]
    public void DirectWithAProfileDryRunPrintsTheScenePlan()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        Run("init", tmp.Path, "--no-interactive", "--name", "Notes", "--url", "http://x/", "--scene", "welcome: Hi");
        cap.Read();
        var rc = Run("direct", tmp.File("tramoya.toml"), "--out", tmp.File("out"), "--dry-run");
        Assert.Equal(0, rc);
        var @out = cap.Out;
        Assert.Contains("1 scenes at http://x/", @out);
        Assert.Contains("welcome", @out);
        Assert.False(Directory.Exists(tmp.File("out")));
    }

    [Fact]
    public void DirectWithAProfileRunsTheGenericDirector()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        Run("init", tmp.Path, "--no-interactive", "--name", "Notes", "--url", "http://x/", "--scene", "welcome: Hi");
        var seen = new Dictionary<string, object?>();
        var real = Cli.RunProfile;
        var browser = Cli.EnsureBrowser;
        Cli.RunProfile = (profile, @out, pace, rehearse, lang, url, launch, storageState, headless) =>
        {
            seen["name"] = profile.Name;
            seen["out"] = @out;
            seen["rehearse"] = rehearse;
            seen["url"] = url;
            seen["headless"] = headless;
            return new Result(@out);
        };
        // A fake director needs no Chromium: the check is patched like run_profile is.
        Cli.EnsureBrowser = () => { seen["browser"] = true; return null; };
        try
        {
            var rc = Run("direct", tmp.File("tramoya.toml"), "--out", tmp.File("out"), "--rehearse", "--url", "http://staging/", "--headed");
            Assert.Equal(0, rc);
        }
        finally
        {
            Cli.RunProfile = real;
            Cli.EnsureBrowser = browser;
        }
        Assert.Equal(true, seen["browser"]);
        Assert.Equal("Notes", seen["name"]);
        Assert.Equal(true, seen["rehearse"]);
        Assert.Equal("http://staging/", seen["url"]);
        Assert.Equal(false, seen["headless"]);
    }

    [Fact]
    public void ImportAppendsRecordedScenesToTheProfile()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        Run("init", tmp.Path, "--no-interactive", "--name", "Notes", "--url", "http://x/", "--scene", "welcome: Hi");
        var flow = tmp.File("flow.json");
        WriteJson(flow, new JsonObject
        {
            ["steps"] = new JsonArray
            {
                new JsonObject { ["type"] = "navigate", ["url"] = "http://x/index.html" },
                new JsonObject { ["type"] = "click", ["selectors"] = new JsonArray(new JsonArray("#new")) },
                new JsonObject { ["type"] = "navigate", ["url"] = "http://x/settings.html" },
                new JsonObject { ["type"] = "click", ["selectors"] = new JsonArray(new JsonArray("#save")) },
            },
        });
        cap.Read();
        var rc = Run("import", flow, "--into", tmp.File("tramoya.toml"));
        Assert.Equal(0, rc);
        var profile = ProfileFile.Load(tmp.File("tramoya.toml"));
        Assert.Equal(new[] { "welcome", "index", "settings" }, profile.Scenes.Select(s => s.Name));
        Assert.Equal(new[] { "click #new", "say" }, profile.Scenes[1].Steps);
        Assert.Equal(new Dictionary<string, string> { ["en"] = "" }, profile.Scenes[1].Narration);
        // the script follows the profile
        var script = JsonNode.Parse(File.ReadAllText(tmp.File("script.json")))!.AsObject();
        Assert.True(script.ContainsKey("scene-settings"));
        Assert.Contains("2 scenes", cap.Read().Out);
    }

    [Fact]
    public void ImportIntoAMissingProfileCreatesOneFromTheRecording()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        var flow = tmp.File("flow.json");
        WriteJson(flow, new JsonObject
        {
            ["steps"] = new JsonArray
            {
                new JsonObject { ["type"] = "navigate", ["url"] = "http://x/index.html" },
                new JsonObject { ["type"] = "click", ["selectors"] = new JsonArray(new JsonArray("#new")) },
            },
        });
        var into = Path.Combine(tmp.Path, "demo", "tramoya.toml"); // the folder does not exist yet
        var rc = Run("import", flow, "--into", into, "--name", "Notes");
        Assert.Equal(0, rc);
        var profile = ProfileFile.Load(into);
        Assert.Equal("http://x/index.html", profile.Url);
        Assert.Equal("Notes", profile.Name);
    }

    [Fact]
    public void CaptureDryRunPrintsTheCodegenCommand()
    {
        using var cap = new Capture();
        var rc = Run("capture", "http://x/", "--out", "flow.py", "--dry-run");
        Assert.Equal(0, rc);
        Assert.Contains("playwright codegen", cap.Out);
        Assert.Contains("--target python", cap.Out);
        Assert.Contains("http://x/", cap.Out);
    }

    [Fact]
    public void LoginDryRunSaysWhereTheSessionGoes()
    {
        using var cap = new Capture();
        Assert.Equal(0, Run("login", "http://x/", "--dry-run"));
        Assert.Contains(".tramoya/auth.json", cap.Out);
    }

    [Fact]
    public void SnapshotsDiffExitsNonzeroWhenASceneChanged()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        Snapshots.WriteSnapshot(tmp.File("a"), "welcome", "- heading Notes\n");
        Snapshots.WriteSnapshot(tmp.File("b"), "welcome", "- heading Notas\n");
        Assert.Equal(0, Run("snapshots", "diff", tmp.File("a"), tmp.File("a")));
        var rc = Run("snapshots", "diff", tmp.File("a"), tmp.File("b"));
        Assert.Equal(1, rc);
        Assert.Contains("welcome: changed", cap.Out);
    }

    // --- the Windows side of the port --------------------------------------------------

    [Fact]
    public void VersionPrintsTheAssemblyVersion()
    {
        using var cap = new Capture();
        Assert.Equal(0, Run("--version"));
        Assert.Equal("tramoya 1.0.0", cap.Out.Trim());
    }

    [Fact]
    public void UnknownFlagIsAUsageError()
    {
        using var cap = new Capture();
        Assert.Equal(2, Run("marks", "x.json", "--bogus"));
        Assert.Contains("usage: tramoya marks", cap.Err);
        Assert.Contains("unrecognized arguments: --bogus", cap.Err);
        Assert.Equal(2, Run("assemble"));
        Assert.Contains("the following arguments are required: --video, --marks, --voice-dir, --out", cap.Err);
    }

    [Fact]
    public void DirectWithAProfileStopsWhenChromiumCannotBeInstalled()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        Run("init", tmp.Path, "--no-interactive", "--name", "Notes", "--url", "http://x/", "--scene", "welcome: Hi");
        var real = Cli.RunProfile;
        var browser = Cli.EnsureBrowser;
        var directed = false;
        Cli.RunProfile = (_, @out, _, _, _, _, _, _, _) => { directed = true; return new Result(@out); };
        Cli.EnsureBrowser = () => { Console.Error.WriteLine("No he podido descargar el navegador Chromium: prueba."); return 2; };
        try
        {
            Assert.Equal(2, Run("direct", tmp.File("tramoya.toml"), "--out", tmp.File("out")));
        }
        finally
        {
            Cli.RunProfile = real;
            Cli.EnsureBrowser = browser;
        }
        Assert.False(directed);
        Assert.Contains("Chromium", cap.Err);
    }

    [Fact]
    public void BrowserWithoutInstallIsAUsageError()
    {
        using var cap = new Capture();
        Assert.Equal(2, Run("browser"));
        Assert.Contains("tramoya browser install", cap.Err);
        Assert.Equal(2, Run("browser", "uninstall"));
    }

    [Fact]
    public void ImportToleratesALanguageListedTwice()
    {
        // `{lang: "" for lang in languages}` in Python keeps one entry; ToDictionary would throw.
        using var tmp = new TempDir();
        using var cap = new Capture();
        Run("init", tmp.Path, "--no-interactive", "--name", "Notes", "--url", "http://x/", "--scene", "welcome: Hi");
        var toml = tmp.File("tramoya.toml");
        File.WriteAllText(toml, File.ReadAllText(toml).Replace("languages = [\"en\"]", "languages = [\"en\", \"en\"]"));
        Assert.Equal(new[] { "en", "en" }, ProfileFile.Load(toml).Languages);
        var flow = tmp.File("flow.json");
        WriteJson(flow, new JsonObject
        {
            ["steps"] = new JsonArray
            {
                new JsonObject { ["type"] = "navigate", ["url"] = "http://x/index.html" },
                new JsonObject { ["type"] = "click", ["selectors"] = new JsonArray(new JsonArray("#new")) },
            },
        });
        Assert.Equal(0, Run("import", flow, "--into", toml));
        var profile = ProfileFile.Load(toml);
        Assert.Equal(new Dictionary<string, string> { ["en"] = "" }, profile.Scenes[1].Narration);
    }

    [Fact]
    public void RecordWithoutDryRunExplainsItIsLinuxOnly()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        Assert.Equal(2, Run("record", "--out", tmp.File("take.mp4"), "--geometry", "0,0 10x10"));
        Assert.Contains("wf-recorder", cap.Err);
    }

    // --- the assistant's commands (tests/test_assistant.py: test_cli_*) ----------------

    private static readonly string PlanJson = new JsonObject
    {
        ["summary"] = "Cut the take by its marks and add the voice.",
        ["steps"] = new JsonArray
        {
            new JsonObject
            {
                ["title"] = "Check the scene table", ["why"] = "See what the marks say before cutting.",
                ["command"] = new JsonArray("tramoya", "marks", "marks.json"),
            },
            new JsonObject
            {
                ["title"] = "Assemble the video", ["why"] = "One clip per scene, voice fitted.",
                ["command"] = new JsonArray("tramoya", "assemble", "--video", "take.mp4", "--marks", "marks.json",
                                            "--voice-dir", "voices/en", "--out", "demo.mp4"),
            },
        },
    }.ToJsonString(Json.Compact);

    private sealed class FakeBackend : IBackend
    {
        private readonly string _reply;
        public FakeBackend(string reply) => _reply = reply;
        public string Complete(string system, string user) => _reply;
    }

    /// <summary>The module patches of the assistant tests: a canned backend and a recording step runner.</summary>
    private sealed class AssistantPatch : IDisposable
    {
        private readonly Func<string?, string?, IBackend> _pick = Cli.PickBackend;
        private readonly Func<string[], int> _run = Cli.RunStep;
        public List<string[]> Ran { get; } = new();

        public AssistantPatch()
        {
            Cli.PickBackend = (_, _) => new FakeBackend(PlanJson);
            Cli.RunStep = argv => { Ran.Add(argv); return 0; };
        }

        public void Dispose()
        {
            Cli.PickBackend = _pick;
            Cli.RunStep = _run;
        }
    }

    [Fact]
    public void CliAskPrintsThePlan()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        using var patch = new AssistantPatch();
        using var cwd = new Chdir(tmp.Path);
        Assert.Equal(0, Run("ask", "make the video"));
        Assert.Contains("1. Check the scene table", cap.Out);
        Assert.Contains("$ tramoya assemble", cap.Out);
    }

    [Fact]
    public void CliDoRunsThePlanWithYes()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        using var patch = new AssistantPatch();
        using var cwd = new Chdir(tmp.Path);
        foreach (var name in new[] { "take.mp4", "marks.json" })
            File.WriteAllText(tmp.File(name), "");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "voices", "en"));
        Assert.Equal(0, Run("do", "make the video", "--yes"));
        Assert.Equal(new[] { "marks", "assemble" }, patch.Ran.Select(r => r[0]));
    }

    [Fact]
    public void CliDoWithoutYesAsksAndANoRunsNothing()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        using var patch = new AssistantPatch();
        using var cwd = new Chdir(tmp.Path);
        using var stdin = new Stdin("n");
        Assert.Equal(1, Run("do", "make the video"));
        Assert.Empty(patch.Ran);
    }

    [Fact]
    public void CliDoRefusesAPlanThatFailsPreflight()
    {
        using var tmp = new TempDir();
        using var cap = new Capture();
        using var patch = new AssistantPatch();
        using var cwd = new Chdir(tmp.Path); // empty: take.mp4 and marks.json are missing
        Assert.Equal(1, Run("do", "make the video", "--yes"));
        Assert.Empty(patch.Ran);
        Assert.Contains("does not exist", cap.Out);
    }
}
