// The `tramoya` command line (port of cli.py).
//
// Every subcommand that would invoke ffmpeg or a recorder takes `--dry-run` and prints
// the exact argv instead, so a whole pipeline can be checked without paying a single
// encode. The subcommands, flags, defaults and printed text are those of the Python
// version; where Windows cannot do what Linux does (wf-recorder, Python director
// scripts) the command says so and exits with 2, but the `--dry-run` echoes stay
// identical so the two versions can be compared line by line.
//
// Every command that opens a browser (`direct` with a profile, `login`, `capture`,
// `easy`, the `easy-engine` workers) first makes sure Playwright's Chromium is there
// (Browsers.EnsureAsync); `--dry-run` never installs anything.
//
// What the Python tests monkeypatch on the module (`pick_backend`, `_run_step`,
// `run_profile`, `sys.stdin.isatty`) is a swappable static delegate here, and so is
// the browser check.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Tramoya.Core;
using Tramoya.Stage;
using Tramoya.Voice;

[assembly: InternalsVisibleTo("Tramoya.Tests")]

namespace Tramoya.App;

internal static class Cli
{
    private const string Doc =
        "The `tramoya` command line. Every subcommand that would invoke ffmpeg or a recorder " +
        "takes `--dry-run` and prints the exact argv instead, so a whole pipeline can be " +
        "checked without paying a single encode.";

    // --- seams for the tests -----------------------------------------------------------

    internal delegate Result RunProfileFn(
        Profile profile, string @out, double? pace, bool rehearse, string? lang,
        string? url, string? launch, string? storageState, bool headless);

    internal static Func<string?, string?, IBackend> PickBackend { get; set; } =
        (name, model) => Assistant.PickBackend(name, model: model);

    internal static Func<string[], int> RunStep { get; set; } = RunStepDefault;

    internal static RunProfileFn RunProfile { get; set; } =
        (profile, @out, pace, rehearse, lang, url, launch, storageState, headless) =>
            Director.RunProfileAsync(profile, @out, pace, rehearse, lang, url, launch, storageState, headless)
                .GetAwaiter().GetResult();

    internal static Func<bool> StdinIsTty { get; set; } = () => !Console.IsInputRedirected;

    /// <summary>Chromium check before a browser command: null when it is there, else the exit code after printing why.</summary>
    internal static Func<int?> EnsureBrowser { get; set; } = EnsureBrowserDefault;

    private sealed class StderrProgress : IProgress<string>
    {
        public void Report(string value) => Console.Error.WriteLine(value);
    }

    private static int? EnsureBrowserDefault()
    {
        var error = Browsers.EnsureAsync(new StderrProgress(), CancellationToken.None).GetAwaiter().GetResult();
        if (error is null)
            return null;
        Console.Error.WriteLine(error);
        return 2;
    }

    /// <summary>`tramoya browser install`: the first-run download, runnable by hand (publish.ps1 can use it too).</summary>
    private static int CmdBrowser(string[] rest)
    {
        if (rest.Length == 1 && rest[0] == "install")
        {
            if (Browsers.Installed())
            {
                Console.WriteLine($"Chromium ya está en {Paths.Navegador}");
                return 0;
            }
            if (EnsureBrowser() is { } rc)
                return rc;
            Console.WriteLine($"Chromium instalado en {Paths.Navegador}");
            return 0;
        }
        Console.Error.WriteLine("usage: tramoya browser install");
        return 2;
    }

    // --- helpers -----------------------------------------------------------------------

    private static void PrintCalls(Runner runner)
    {
        foreach (var argv in runner.Calls)
            Console.WriteLine(Shell.Join(argv));
    }

    private static List<(double Start, double End)> ParseRanges(string text)
    {
        var ranges = new List<(double, double)>();
        foreach (var chunk in text.Split(','))
        {
            var parts = chunk.Split('-', 2);
            if (parts.Length != 2)
                throw new ArgumentException($"not enough values to unpack (expected 2, got {parts.Length})");
            ranges.Add((double.Parse(parts[0], CultureInfo.InvariantCulture),
                        double.Parse(parts[1], CultureInfo.InvariantCulture)));
        }
        return ranges;
    }

    /// <summary>Python's <c>f"{x:+.2f}"</c>.</summary>
    private static string Signed(double x, int decimals) => (x < 0 ? "" : "+") + Fmt.F(x, decimals);

    private static string Version()
    {
        var assembly = typeof(Cli).Assembly;
        var info = System.Reflection.CustomAttributeExtensions
                       .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString(3) ?? "0";
        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }

    private static string TempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void RemoveDir(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // --- subcommands -------------------------------------------------------------------

    private static int CmdMarks(ParsedArgs args)
    {
        var offset = args.Double("offset", 0.0);
        var marks = MarkKeys.LoadMarks(args.GetRequired("marks"), offset);
        var scenes = MarkKeys.SceneBounds(marks);
        var waits = MarkKeys.WaitWindows(marks);
        Console.WriteLine($"{scenes.Count} scenes, {waits.Count} waits, offset {Signed(offset, 2)}s");
        foreach (var (name, (start, end)) in scenes)
            Console.WriteLine($"  {name,-20} {Fmt.F(start, 2),8} -> {Fmt.F(end, 2),8}  ({Fmt.F(end - start, 1),6}s)");
        foreach (var (start, end) in waits)
            Console.WriteLine($"  {"(wait)",-20} {Fmt.F(start, 2),8} -> {Fmt.F(end, 2),8}  ({Fmt.F(end - start, 1),6}s)");
        return 0;
    }

    private static double OffsetFor(string video, double? explicitOffset)
    {
        if (explicitOffset is { } value)
            return value;
        var sidecar = Path.Combine(Path.GetDirectoryName(video) ?? "", Path.GetFileName(video) + ".offset.json");
        return File.Exists(sidecar) ? Sidecar.ReadSidecar(sidecar) : 0.0;
    }

    private static int CmdAssemble(ParsedArgs args)
    {
        var dryRun = args.Flag("dry_run");
        var runner = new Runner(dryRun: dryRun);
        var video = args.GetRequired("video");
        var @out = args.GetRequired("out");
        var offset = OffsetFor(video, args.Double("offset"));
        var table = Assembly.AssembleMarks(
            runner, video, args.GetRequired("marks"), args.GetRequired("voice_dir"), @out,
            offset: offset, lang: args.GetRequired("lang"));
        if (dryRun)
            PrintCalls(runner);
        else
        {
            foreach (var (name, raw, final) in table)
                Console.WriteLine($"scene {name}: {Fmt.F(raw, 1),6}s raw -> {Fmt.F(final, 1),6}s final");
            Console.WriteLine($"assembled {@out}");
        }
        return 0;
    }

    private static int CmdDeck(ParsedArgs args)
    {
        var slides = args.GetRequired("slides");
        var manifest = Json.LoadNode(Path.Combine(slides, "index.json"))!.AsObject();
        var pages = manifest["pages"]!.AsObject()
            .ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>(), StringComparer.Ordinal);
        var order = pages.Keys.OrderBy(n => pages[n], StringComparer.Ordinal).ToList();
        var voiceDir = args.GetRequired("voice_dir");
        var voices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in order)
        {
            var wav = Path.Combine(voiceDir, $"{n}.wav");
            if (File.Exists(wav))
                voices[n] = wav;
        }
        var durations = voices.ToDictionary(kv => kv.Key, kv => Narration.WavSeconds(kv.Value), StringComparer.Ordinal);
        var plan = Assembly.PlanSegments(
            pages, order, durations, slides, args.Get("clip"), args.Double("clip_duration", 0.0),
            voices: voices, clipPage: args.GetRequired("clip_page"));
        foreach (var seg in plan)
            Console.WriteLine($"  {seg.Kind,-5} {seg.Name,-20} {Fmt.F(seg.Duration, 1),6}s  {Path.GetFileName(seg.Source)}");
        var dryRun = args.Flag("dry_run");
        var runner = new Runner(dryRun: dryRun);
        var tmp = TempDir("tramoya-deck-");
        try
        {
            Assembly.AssembleDeck(runner, plan, tmp, args.GetRequired("out"));
        }
        finally
        {
            RemoveDir(tmp);
        }
        if (dryRun)
            PrintCalls(runner);
        else
            Console.WriteLine($"assembled {args.GetRequired("out")}");
        return 0;
    }

    private static int CmdCaptionsBurn(ParsedArgs args)
    {
        var dryRun = args.Flag("dry_run");
        var runner = new Runner(dryRun: dryRun);
        var video = args.GetRequired("video");
        var tmp = TempDir("tramoya-cap-");
        try
        {
            if (args.Get("trim") is { } trim)
            {
                var trimmed = Path.Combine(tmp, "trimmed.mp4");
                foreach (var argv in Captions.TrimPlan(ParseRanges(trim), video, tmp, trimmed))
                    runner.Run(argv);
                video = trimmed;
            }
            runner.Run(Captions.BurnArgv(video, args.GetRequired("subtitles"), args.GetRequired("out")));
        }
        finally
        {
            RemoveDir(tmp);
        }
        if (dryRun)
            PrintCalls(runner);
        return 0;
    }

    private static int CmdMusicBed(ParsedArgs args)
    {
        var @out = args.GetRequired("out");
        Audio.RenderWav(Audio.BuildLoop(), @out);
        Console.WriteLine($"bed written to {@out}");
        return 0;
    }

    private static int CmdMusicMix(ParsedArgs args)
    {
        var dryRun = args.Flag("dry_run");
        var runner = new Runner(dryRun: dryRun);
        runner.Run(Audio.MixArgv(args.GetRequired("video"), args.GetRequired("bed"), args.GetRequired("out"),
                                 duck: !args.Flag("solo")));
        if (dryRun)
            PrintCalls(runner);
        return 0;
    }

    private static (List<Cue> Cues, double Total) LoadCues(ParsedArgs args)
    {
        var (cues, total) = Cues.ParseWithTotal(File.ReadAllText(args.GetRequired("cues"), System.Text.Encoding.UTF8));
        var offset = 0.0;
        if (args.Get("meta") is { } metaPath && args.Double("duration") is { } duration)
        {
            var meta = Cues.ParseMeta(File.ReadAllText(metaPath, System.Text.Encoding.UTF8));
            offset = Cues.OffsetFromDuration(duration, double.Parse(meta["elapsed"], CultureInfo.InvariantCulture));
        }
        return (Cues.Shift(cues, offset), total + offset);
    }

    private static int CmdCuesMarks(ParsedArgs args)
    {
        var (cues, _) = LoadCues(args);
        var rows = MarkKeys.ToJson(Cues.ToMarks(cues));
        var @out = args.GetRequired("out");
        File.WriteAllText(@out, Json.Dump(rows, trailingNewline: true));
        Console.WriteLine($"{rows.Count} marks written to {@out}");
        return 0;
    }

    private static int CmdCuesAss(ParsedArgs args)
    {
        var (cues, total) = LoadCues(args);
        var text = Cues.ToAss(cues, total, args.Int("width")!.Value, args.Int("height")!.Value,
                              font: args.GetRequired("font"));
        var @out = args.GetRequired("out");
        File.WriteAllText(@out, text, new System.Text.UTF8Encoding(false));
        Console.WriteLine($"{cues.Count} cues written to {@out}");
        return 0;
    }

    /// <summary>wf-recorder's argv as record.py builds it: audio is opt-in, and so is the codec flag.</summary>
    internal static string[] WfRecorderArgv(string @out, string geometry, int fps = 10, string? audioSink = null,
                                            string codec = "aac")
    {
        var argv = new List<string> { "wf-recorder", "-r", fps.ToString(CultureInfo.InvariantCulture), "-g", geometry };
        if (!string.IsNullOrEmpty(audioSink))
            argv.AddRange(new[] { $"--audio={audioSink}.monitor", "-C", codec });
        argv.AddRange(new[] { "-f", @out });
        return argv.ToArray();
    }

    private static int CmdRecord(ParsedArgs args)
    {
        if (args.Flag("dry_run"))
        {
            var argv = WfRecorderArgv(args.GetRequired("out"), args.GetRequired("geometry"),
                                      fps: args.Int("fps")!.Value, audioSink: args.Get("audio_sink"));
            Console.WriteLine(Shell.Join(argv));
            return 0;
        }
        Console.Error.WriteLine("record needs wf-recorder, which only exists on Linux");
        return 2;
    }

    private static bool IsProfile(string path) => path.EndsWith(".toml", StringComparison.Ordinal);

    private static int CmdDirect(ParsedArgs args)
    {
        var script = args.GetRequired("script");
        var @out = args.GetRequired("out");
        var pace = args.Double("pace");
        var lang = args.Get("lang");
        var dryRun = args.Flag("dry_run");
        if (IsProfile(script))
        {
            var profile = ProfileFile.Load(script);
            if (dryRun)
            {
                Console.WriteLine(Director.Describe(profile));
                return 0;
            }
            if (EnsureBrowser() is { } missing)
                return missing;
            var result = RunProfile(
                profile, @out, pace, args.Flag("rehearse"), lang,
                args.Get("url"), args.Get("launch"), args.Get("storage_state"),
                headless: !args.Flag("headed"));
            var chosen = lang ?? profile.Languages[0];
            Console.WriteLine($"take written to {result.Take}; marks in {result.Marks}; " +
                              $"{result.Scenes.Count} scenes, snapshots in {Path.Combine(result.Out, "snapshots")}");
            Console.WriteLine($"next: tramoya tts --script {Path.Combine(result.Out, "script.json")} " +
                              $"--lang {chosen} --out-dir {Path.Combine(result.Out, "voices")}/{chosen}");
            return 0;
        }
        foreach (var flag in new[] { "url", "launch", "storage_state" })
        {
            if (!string.IsNullOrEmpty(args.Get(flag)))
            {
                Console.Error.WriteLine($"--{flag.Replace('_', '-')} applies to a tramoya.toml profile, " +
                                        "not a director script");
                return 2;
            }
        }
        var argv = new List<string>
        {
            // `args.pace or 1.0`: None and 0.0 both fall back.
            "python", script, "--out", @out, "--pace", Fmt.Py(pace is { } p && p != 0 ? p : 1.0),
            "--lang", lang ?? "en",
        };
        if (args.Flag("rehearse"))
            argv.Insert(argv.Count - 2, "--rehearse");
        if (dryRun)
        {
            Console.WriteLine(Shell.Join(argv));
            return 0;
        }
        Console.Error.WriteLine("direct: Python director scripts are not supported on Windows; " +
                                "describe the scenes in tramoya.toml and run `tramoya direct tramoya.toml`");
        return 2;
    }

    private static int CmdTts(ParsedArgs args)
    {
        var script = Json.LoadNode(args.GetRequired("script"))!.AsObject();
        var lang = args.GetRequired("lang");
        var outDir = args.GetRequired("out_dir");
        // The Python picks the Kokoro voice of the language; here it is the Windows voice
        // of the language, and without one the narration must not come out in Spanish.
        if (Sapi.VozPara(lang) is null)
        {
            Console.Error.WriteLine($"tts: no Windows voice installed for '{lang}'; installed: " +
                                    string.Join(", ", Sapi.Voces()));
            return 2;
        }
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (var (key, value) in script)
        {
            var text = value!.AsObject()[lang]?.GetValue<string>()
                       ?? throw new KeyNotFoundException(lang);
            var path = Path.Combine(outDir, $"{key}.wav");
            texts[path] = text;
            paths.Add(path);
        }
        Directory.CreateDirectory(outDir);
        Sapi.Hablar(texts, idioma: lang);
        foreach (var p in paths)
            Console.WriteLine(p);
        return 0;
    }

    private static List<string> VisibleFiles(int limit = 60)
    {
        var files = new List<string>();
        foreach (var path in Directory.EnumerateFiles(".", "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(".", path);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(part => part.StartsWith('.')))
                continue;
            files.Add(relative);
        }
        files.Sort(StringComparer.Ordinal);
        return files.Take(limit).ToList();
    }

    private static int RunStepDefault(string[] argv)
    {
        Console.WriteLine($"$ tramoya {Shell.Join(argv)}");
        Console.Out.Flush();
        return Run(argv);
    }

    private static Profile? CurrentProfile() =>
        File.Exists("tramoya.toml") ? ProfileFile.Load("tramoya.toml") : null;

    private static Plan PlanFor(ParsedArgs args)
    {
        var backend = PickBackend(args.Get("backend"), args.Get("model"));
        return Assistant.Ask(backend, args.GetRequired("request"), VisibleFiles(), profile: CurrentProfile());
    }

    private static int CmdAsk(ParsedArgs args)
    {
        Console.WriteLine(Assistant.RenderPlan(PlanFor(args)));
        return 0;
    }

    private static int CmdDo(ParsedArgs args)
    {
        var plan = PlanFor(args);
        Console.WriteLine(Assistant.RenderPlan(plan));
        if (plan.Steps.Count == 0)
            return 1;
        var problems = Assistant.Preflight(plan);
        if (problems.Count > 0)
        {
            Console.WriteLine("\nnot running:");
            foreach (var problem in problems)
                Console.WriteLine($"  {problem}");
            return 1;
        }
        if (!args.Flag("yes"))
        {
            Console.Write($"\nRun these {plan.Steps.Count} steps? [y/N] ");
            var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (answer is not ("y" or "yes"))
            {
                Console.WriteLine("nothing run");
                return 1;
            }
        }
        Console.WriteLine();
        var codes = Assistant.RunPlan(plan, runner: RunStep, dryRun: args.Flag("dry_run"));
        return codes.Count > 0 ? codes[^1] : 0;
    }

    private static int CmdInit(ParsedArgs args)
    {
        var directory = args.GetRequired("directory");
        var name = args.Get("name");
        var url = args.Get("url");
        var launch = args.Get("launch");
        var scenes = args.List("scene");
        var interactive = !args.Flag("no_interactive") && StdinIsTty()
                          && string.IsNullOrEmpty(name) && string.IsNullOrEmpty(url)
                          && string.IsNullOrEmpty(launch) && scenes is null;
        IAnswers answers = interactive
            ? new TtyAnswers()
            : new FlagAnswers(name: name, url: url, launch: launch, login: args.Flag("login"),
                              languages: args.Get("lang"), scenes: scenes);
        if (interactive)
            Console.WriteLine("tramoya init: a few questions, then a project that makes a video.\n");
        Profile profile;
        List<string> written;
        try
        {
            profile = Wizard.Interview(answers);
            written = Wizard.Scaffold(profile, directory, force: args.Flag("force"));
        }
        catch (Exception exc) when (exc is MissingAnswer or IOException)
        {
            Console.Error.WriteLine($"init: {exc.Message}");
            return 2;
        }
        foreach (var path in written)
            Console.WriteLine($"wrote {path}");
        if (!string.IsNullOrEmpty(profile.StorageState))
            Console.WriteLine($"\nsign in once:  tramoya login {profile.Url} --out " +
                              $"{Path.Combine(directory, profile.StorageState)}");
        Console.WriteLine($"\nnext:  cd {directory} && tramoya direct tramoya.toml --out out");
        Console.WriteLine("       (or record the scenes first: tramoya capture <url> --into tramoya.toml)");
        return 0;
    }

    private static int CmdImport(ParsedArgs args) =>
        Import(args.GetRequired("recording"), args.GetRequired("into"), args.Get("name"), args.Get("url"));

    private static int Import(string recording, string intoPath, string? name, string? url)
    {
        var scenes = Recording.ScenesFromFile(recording);
        // `into.parent`: "." when --into is a bare file name.
        var parent = Path.GetDirectoryName(intoPath) is { Length: > 0 } dir ? dir : ".";
        Profile profile;
        if (File.Exists(intoPath))
            profile = ProfileFile.Load(intoPath);
        else
        {
            var firstUrl = FirstUrl(recording);
            if (string.IsNullOrEmpty(name) || (string.IsNullOrEmpty(url) && firstUrl is null))
            {
                Console.Error.WriteLine("import: no profile yet; pass --name (and --url when the recording has no " +
                                        "navigation) to create one");
                return 2;
            }
            profile = new Profile(name: name!, url: string.IsNullOrEmpty(url) ? firstUrl : url, root: parent);
        }
        var known = profile.Scenes.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var scene in scenes)
        {
            var sceneName = scene.Name;
            var n = 2;
            while (known.Contains(sceneName))
            {
                sceneName = $"{scene.Name}-{n}";
                n++;
            }
            known.Add(sceneName);
            // `{lang: "" for lang in profile.languages}`: a language listed twice just
            // overwrites itself, where ToDictionary would throw.
            var narration = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var language in profile.Languages)
                narration[language] = "";
            profile.Scenes.Add(new Scene(sceneName, scene.Label, narration, scene.Steps));
            added++;
        }
        Directory.CreateDirectory(parent);
        File.WriteAllText(intoPath, ProfileFile.Dump(profile), new System.Text.UTF8Encoding(false));
        var script = parent == "." ? "script.json" : Path.Combine(parent, "script.json");
        File.WriteAllText(script, Json.Dump(profile.Script(), trailingNewline: true), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"{added} scenes added to {intoPath}; narration keys in {script}");
        Console.WriteLine("fill in the narration, then: tramoya direct tramoya.toml --out out");
        return 0;
    }

    private static string? FirstUrl(string recording)
    {
        var text = File.ReadAllText(recording, System.Text.Encoding.UTF8);
        var m = System.Text.RegularExpressions.Regex.Match(text, "\"url\":\\s*\"([^\"]+)\"");
        if (!m.Success)
            m = System.Text.RegularExpressions.Regex.Match(text, "page\\.goto\\(\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    internal static string[] CodegenArgv(string url, string @out) =>
        new[] { "python", "-m", "playwright", "codegen", "--target", "python", "-o", @out, url };

    private static int CmdCapture(ParsedArgs args)
    {
        var url = args.GetRequired("url");
        var @out = args.GetRequired("out");
        var argv = CodegenArgv(url, @out);
        if (args.Flag("dry_run"))
        {
            // The echo is the Python one (`python -m playwright codegen ...`) so the two
            // versions compare line by line; what runs here is the same codegen through
            // the .NET package's driver.
            Console.WriteLine(Shell.Join(argv));
            return 0;
        }
        if (EnsureBrowser() is { } missing)
            return missing;
        Console.WriteLine("a browser opens: click through the demo, then close it");
        Console.Out.Flush();
        var rc = Codegen(new[] { "codegen", "--target", "python", "-o", @out, url });
        if (rc != 0 || !File.Exists(@out))
        {
            Console.Error.WriteLine($"capture: playwright codegen failed ({rc}); " +
                                    "is Chromium installed? tramoya browser install");
            return rc != 0 ? rc : 1;
        }
        Console.WriteLine($"recording written to {@out}");
        if (args.Get("into") is { Length: > 0 } into)
            return Import(@out, into, args.Get("name"), null);
        return 0;
    }

    /// <summary>Playwright's codegen through the package's own driver (`playwright.ps1 codegen ...`); -1 when the driver cannot start.</summary>
    private static int Codegen(string[] driverArgs)
    {
        try
        {
            return Microsoft.Playwright.Program.Main(driverArgs);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"capture: cannot start the Playwright driver ({e.Message})");
            return -1;
        }
    }

    private static int CmdLogin(ParsedArgs args)
    {
        var url = args.GetRequired("url");
        var @out = args.GetRequired("out");
        if (args.Flag("dry_run"))
        {
            Console.WriteLine($"a browser would open {url}; when you press Enter the session " +
                              $"(cookies and local storage, never the password) is saved to {@out}");
            return 0;
        }
        if (EnsureBrowser() is { } missing)
            return missing;
        if (Path.GetDirectoryName(Path.GetFullPath(@out)) is { } parent)
            Directory.CreateDirectory(parent);
        LoginAsync(url, @out).GetAwaiter().GetResult();
        Console.WriteLine($"session saved to {@out}; point [app] storage_state at it");
        return 0;
    }

    private static async Task LoginAsync(string url, string @out)
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false }).ConfigureAwait(false);
        var context = await browser.NewContextAsync().ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await page.GotoAsync(url).ConfigureAwait(false);
        Console.WriteLine("sign in in the browser, then press Enter here");
        Console.Out.Flush();
        Console.ReadLine();
        await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = @out }).ConfigureAwait(false);
        await browser.CloseAsync().ConfigureAwait(false);
    }

    private static int CmdEasy(ParsedArgs args) => EasyServerRun(args.Flag("no_window"));

    /// <summary>`tramoya easy`: serve the app; with `--no-window` print the URL and stay up, else open the window.</summary>
    private static int EasyServerRun(bool noWindow)
    {
        if (noWindow)
        {
            // The window asks for the browser with its own dialog; here it is the terminal.
            if (EnsureBrowser() is { } missing)
                return missing;
            return Tramoya.Easy.Server.Run(EasyHost.Options(), noWindow: true);
        }
        return Program.RunWindow();
    }

    /// <summary>The hidden `easy-engine grabar|video|voces|oir|pasos` worker the server launches as a child process.</summary>
    private static int EasyEngineMain(string[] rest) => Tramoya.Easy.Engine.Main(rest);

    private static int CmdSnapshotsDiff(ParsedArgs args)
    {
        var problems = Snapshots.DiffSnapshots(args.GetRequired("expected"), args.GetRequired("actual"));
        if (problems.Count == 0)
        {
            Console.WriteLine("snapshots match");
            return 0;
        }
        foreach (var problem in problems)
            Console.WriteLine(problem);
        Console.WriteLine($"\n{problems.Count} scene(s) differ");
        return 1;
    }

    // --- parser ------------------------------------------------------------------------

    private static void AddDryRun(ArgParser p) =>
        p.AddSwitch("--dry-run", help: "print the commands instead of running them");

    internal static ArgParser BuildParser()
    {
        var ap = new ArgParser("tramoya", description: Doc);
        ap.AddVersion($"tramoya {Version()}");

        var p = ap.AddSubparser("command", "marks", help: "print the scene table of a marks file");
        p.Add("marks");
        p.Add("--offset", kind: ArgKind.Float, @default: "0.0");
        p.Handler = CmdMarks;

        p = ap.AddSubparser("command", "assemble", help: "marks + recording + voice clips -> one video");
        p.Add("--video", required: true);
        p.Add("--marks", required: true);
        p.Add("--voice-dir", required: true);
        p.Add("--out", required: true);
        p.Add("--offset", kind: ArgKind.Float, help: "director-vs-recorder offset; defaults to <video>.offset.json");
        p.Add("--lang", @default: "en");
        AddDryRun(p);
        p.Handler = CmdAssemble;

        p = ap.AddSubparser("command", "deck", help: "slide PNGs + narration (+ a clip) -> one video");
        p.Add("--slides", required: true, help: "directory with index.json and PNGs");
        p.Add("--voice-dir", required: true);
        p.Add("--clip");
        p.Add("--clip-duration", kind: ArgKind.Float, @default: "0.0");
        p.Add("--clip-page", @default: "video");
        p.Add("--out", required: true);
        AddDryRun(p);
        p.Handler = CmdDeck;

        p = ap.AddSubparser("command", "captions", help: "subtitle tools");
        var b = p.AddSubparser("captions_command", "burn", help: "burn an .srt/.ass into a video");
        b.Add("--video", required: true);
        b.Add("--subtitles", required: true);
        b.Add("--out", required: true);
        b.Add("--trim", help: "keep only these ranges: 0-120,600-780");
        AddDryRun(b);
        b.Handler = CmdCaptionsBurn;

        p = ap.AddSubparser("command", "music", help: "background bed tools");
        var bed = p.AddSubparser("music_command", "bed", help: "synthesize the looping background bed");
        bed.Add("--out", required: true);
        bed.Handler = CmdMusicBed;
        var mix = p.AddSubparser("music_command", "mix", help: "mix the bed into a video, ducked under the voice");
        mix.Add("--video", required: true);
        mix.Add("--bed", required: true);
        mix.Add("--out", required: true);
        mix.AddSwitch("--solo", help: "flat level, no ducking");
        AddDryRun(mix);
        mix.Handler = CmdMusicMix;

        p = ap.AddSubparser("command", "cues", help: "single-take cue files (timestamp|text)");
        foreach (var (name, fn) in new (string, Func<ParsedArgs, int>)[] { ("marks", CmdCuesMarks), ("ass", CmdCuesAss) })
        {
            var c = p.AddSubparser("cues_command", name);
            c.Add("--cues", required: true);
            c.Add("--meta");
            c.Add("--duration", kind: ArgKind.Float, help: "ffprobe duration of the take, to recover the pre-roll");
            c.Add("--out", required: true);
            if (name == "ass")
            {
                c.Add("--width", kind: ArgKind.Int, required: true);
                c.Add("--height", kind: ArgKind.Int, required: true);
                c.Add("--font", @default: "JetBrainsMono Nerd Font");
            }
            c.Handler = fn;
        }

        p = ap.AddSubparser("command", "record", help: "record the screen with wf-recorder");
        p.Add("--out", required: true);
        p.Add("--geometry", required: true, help: "\"X,Y WxH\"");
        p.Add("--fps", kind: ArgKind.Int, @default: "10");
        p.Add("--audio-sink");
        AddDryRun(p);
        p.Handler = CmdRecord;

        foreach (var (name, fn, helpText) in new (string, Func<ParsedArgs, int>, string)[]
                 {
                     ("ask", CmdAsk, "describe what you want; get the plan of commands, run nothing"),
                     ("do", CmdDo, "describe what you want; confirm the plan, then run it"),
                 })
        {
            p = ap.AddSubparser("command", name, help: helpText);
            p.Add("request", help: "e.g. \"make the video from take.mp4 with the English voice\"");
            p.Add("--backend", choices: new[] { "ollama", "claude" },
                  help: "default: $TRAMOYA_LLM, else claude when $ANTHROPIC_API_KEY is set, else ollama");
            p.Add("--model", help: "model name; default: $TRAMOYA_LLM_MODEL");
            if (name == "do")
            {
                p.AddSwitch("--yes", help: "skip the confirmation");
                AddDryRun(p);
            }
            p.Handler = fn;
        }

        p = ap.AddSubparser("command", "direct", help: "run a director script or an app profile: it drives " +
                                                       "the app on stage and records the take");
        p.Add("script", help: "a Python file built on tramoya.stage, or tramoya.toml");
        p.Add("--out", required: true, help: "where take.webm and marks.json go");
        p.Add("--pace", kind: ArgKind.Float);
        p.AddSwitch("--rehearse", help: "run every scene in seconds");
        p.Add("--lang");
        p.Add("--url", help: "profile only: open this URL instead");
        p.Add("--launch", help: "profile only: start the app with this");
        p.Add("--storage-state", help: "profile only: a session file");
        p.AddSwitch("--headed", help: "profile only: show the browser");
        AddDryRun(p);
        p.Handler = CmdDirect;

        p = ap.AddSubparser("command", "init", help: "ask a few questions and write tramoya.toml, " +
                                                     "script.json and a Makefile");
        p.Add("directory", optional: true, @default: ".");
        p.Add("--name", help: "what the app is called");
        p.Add("--url", help: "the page to open (or, with --launch, the URL to wait for)");
        p.Add("--launch", help: "a command that starts the app, run in the project folder");
        p.AddSwitch("--login", help: "the demo is signed in (tramoya login)");
        p.Add("--lang", help: "narration languages, comma separated (default en)");
        p.AddAppend("--scene", help: "\"name: what it shows\"; repeatable");
        p.AddSwitch("--no-interactive", help: "never prompt; every answer comes from a flag");
        p.AddSwitch("--force", help: "overwrite existing files");
        p.Handler = CmdInit;

        p = ap.AddSubparser("command", "import", help: "turn a Chrome DevTools Recorder export or a playwright " +
                                                       "codegen script into scenes of the profile");
        p.Add("recording", help: "flow.json from DevTools, or flow.py from codegen");
        p.Add("--into", @default: "tramoya.toml");
        p.Add("--name", help: "when the profile does not exist yet: the app's name");
        p.Add("--url", help: "when the profile does not exist yet and the recording has no navigation");
        p.Handler = CmdImport;

        p = ap.AddSubparser("command", "capture", help: "open a browser on the app, record what you click " +
                                                        "(playwright codegen) and import it as scenes");
        p.Add("url");
        p.Add("--out", @default: "flow.py", help: "where the recording goes");
        p.Add("--into", help: "profile to add the scenes to");
        p.Add("--name", help: "app name, when --into does not exist yet");
        AddDryRun(p);
        p.Handler = CmdCapture;

        p = ap.AddSubparser("command", "login", help: "sign in once in a real browser and save the session " +
                                                      "for demos behind a login");
        p.Add("url");
        p.Add("--out", @default: Wizard.SessionFile);
        AddDryRun(p);
        p.Handler = CmdLogin;

        p = ap.AddSubparser("command", "easy", help: "the point-and-click app (Spanish): do the process once in the " +
                                                     "browser, write a sentence per step, get the video",
                            aliases: "facil");
        p.AddSwitch("--no-window", help: "serve the app and print its URL instead of opening a window");
        p.Handler = CmdEasy;

        p = ap.AddSubparser("command", "snapshots", help: "accessibility snapshots written by direct");
        var d = p.AddSubparser("snapshots_cmd", "diff", help: "compare two snapshot folders; exit 1 when a scene changed");
        d.Add("expected");
        d.Add("actual");
        d.Handler = CmdSnapshotsDiff;

        p = ap.AddSubparser("command", "tts", help: "synthesize a narration script with Kokoro");
        p.Add("--script", required: true, help: "JSON: key -> {lang: text}");
        p.Add("--lang", required: true);
        p.Add("--out-dir", required: true);
        p.Handler = CmdTts;
        return ap;
    }

    /// <summary>Python's <c>main(argv)</c>: parse, dispatch, return the exit code.</summary>
    public static int Run(string[] args)
    {
        // Hidden worker entry point, like `python -m tramoya.easy.engine`: not in the help.
        if (args.Length > 0 && args[0] == "easy-engine")
        {
            // `grabar` and `video` open Chromium; the server reads the progress lines on
            // stderr as the trail of a failure, so they never break its JSON protocol.
            if (args.Length > 1 && args[1] is "grabar" or "video" && EnsureBrowser() is { } missing)
                return missing;
            return EasyEngineMain(args.Skip(1).ToArray());
        }
        // Hidden too: `tramoya browser install`, the first-run download by hand.
        if (args.Length > 0 && args[0] == "browser")
            return CmdBrowser(args.Skip(1).ToArray());
        ParsedArgs parsed;
        try
        {
            parsed = BuildParser().Parse(args);
        }
        catch (ArgParseExit exit)
        {
            (exit.Code == 0 ? Console.Out : Console.Error).Write(exit.Message);
            return exit.Code;
        }
        return parsed.Handler!(parsed);
    }
}
