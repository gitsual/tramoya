using System.Text;
using System.Text.RegularExpressions;

namespace Tramoya.Core;

/// <summary>
/// <c>tramoya init</c>: the questions that turn an app into a profile (port of
/// <c>tramoya/wizard.py</c>).
///
/// The interview is linear and every question has a matching flag, so the same run works
/// on a terminal (<see cref="TtyAnswers"/>), from flags in CI (<see cref="FlagAnswers"/>)
/// and in tests (<see cref="ScriptedAnswers"/>). The wizard never asks for a password: a
/// demo behind a sign-in is handled by <c>tramoya login</c>, which saves a browser session
/// file the profile then points at.
/// </summary>
public interface IAnswers
{
    string Ask(string key, string question, string @default = "");
    void Warn(string text);
}

/// <summary>A non-interactive run had no answer for a question; names the flag.</summary>
public sealed class MissingAnswer : ArgumentException
{
    public MissingAnswer(string message) : base(message) { }
}

/// <summary>Answers in order, for tests; records warnings instead of printing.</summary>
public sealed class ScriptedAnswers : IAnswers
{
    private readonly List<string> _replies;
    public List<string> Warnings { get; } = new();

    public ScriptedAnswers(IEnumerable<string> replies)
    {
        _replies = replies.ToList();
    }

    public string Ask(string key, string question, string @default = "")
    {
        if (_replies.Count == 0)
            throw new MissingAnswer($"no scripted answer for {key}");
        var reply = _replies[0];
        _replies.RemoveAt(0);
        return reply != "" || key == "scene" ? reply : @default;
    }

    public void Warn(string text) => Warnings.Add(text);

    public bool Exhausted => _replies.Count == 0;
}

/// <summary>Ask on the terminal; a blank reply takes the default.</summary>
public sealed class TtyAnswers : IAnswers
{
    public string Ask(string key, string question, string @default = "")
    {
        var hint = @default.Length > 0 ? $" [{@default}]" : "";
        Console.Write($"{question}{hint}: ");
        var reply = (Console.ReadLine() ?? throw new EndOfStreamException("EOF when reading a line")).Trim();
        return reply.Length > 0 ? reply : @default;
    }

    public void Warn(string text) => Console.WriteLine($"  ! {text}");
}

/// <summary>
/// Answers taken from flags; a question with no flag is an error, not a prompt, so CI
/// fails naming what to pass.
/// </summary>
public sealed class FlagAnswers : IAnswers
{
    private static readonly Dictionary<string, string> FlagOf = new()
    {
        ["name"] = "--name", ["reach"] = "--url or --launch", ["url"] = "--url or --launch",
        ["launch"] = "--launch", ["ready"] = "--url", ["login"] = "--login",
        ["languages"] = "--lang", ["scene"] = "--scene",
    };

    public Dictionary<string, string> Values { get; } = new();
    public List<string> Warnings { get; } = new();
    private readonly List<string> _scenes;
    private bool _askedScene;

    public FlagAnswers(
        string? name = null,
        string? url = null,
        string? launch = null,
        bool login = false,
        string? languages = null,
        IEnumerable<string>? scenes = null)
    {
        if (!string.IsNullOrEmpty(name))
            Values["name"] = name;
        if (!string.IsNullOrEmpty(launch))
        {
            Values["reach"] = "launch";
            Values["launch"] = launch;
            if (!string.IsNullOrEmpty(url))
                Values["ready"] = url;
        }
        else if (!string.IsNullOrEmpty(url))
        {
            Values["reach"] = "url";
            Values["url"] = url;
        }
        Values["login"] = login ? "y" : "n";
        if (!string.IsNullOrEmpty(languages))
            Values["languages"] = languages;
        _scenes = scenes?.ToList() ?? new List<string>();
    }

    public string Ask(string key, string question, string @default = "")
    {
        if (key == "scene")
        {
            if (_scenes.Count > 0)
            {
                _askedScene = true;
                var scene = _scenes[0];
                _scenes.RemoveAt(0);
                return scene;
            }
            if (!_askedScene)
                throw new MissingAnswer("non-interactive init needs at least one --scene \"name: label\"");
            return "";
        }
        if (Values.TryGetValue(key, out var value))
            return value;
        if (@default.Length > 0)
            return @default;
        throw new MissingAnswer($"non-interactive init needs {(FlagOf.TryGetValue(key, out var flag) ? flag : key)}");
    }

    public void Warn(string text) => Warnings.Add(text);
}

public static class Wizard
{
    public const string SessionFile = ".tramoya/auth.json";

    private static readonly Regex NotSlug = new("[^a-z0-9]+", RegexOptions.Compiled);

    public static string Slug(string text)
    {
        var s = NotSlug.Replace(text.ToLowerInvariant(), "-").Trim('-');
        return s.Length > 0 ? s : "scene";
    }

    private static (string Name, string Label) ParseScene(string reply)
    {
        var colon = reply.IndexOf(':');
        var name = colon < 0 ? reply : reply[..colon];
        var label = colon < 0 ? name.Trim() : reply[(colon + 1)..].Trim();
        return (Slug(name), label.Length > 0 ? label : name.Trim());
    }

    public static Profile Interview(IAnswers answers)
    {
        var name = answers.Ask("name", "What is the app called");
        var reach = answers.Ask("reach", "Is the app already running (url) or should tramoya "
                                         + "launch it (launch)", "url");
        string? launch = null, url;
        if (reach.Trim().ToLowerInvariant().StartsWith('l'))
        {
            launch = answers.Ask("launch", "Command that starts the app, run in this folder");
            url = answers.Ask("ready", "URL to open once it answers");
        }
        else
        {
            url = answers.Ask("url", "URL of the page to open");
        }
        var login = answers.Ask("login", "Does the demo need to be signed in (y/n)", "n");
        var storageState = login.Trim().ToLowerInvariant().StartsWith('y') ? SessionFile : null;
        var langs = answers.Ask("languages", "Narration languages, comma separated", "en");
        var languages = langs.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (languages.Count == 0) languages = new List<string> { "en" };

        var scenes = new List<Scene>();
        var seen = new HashSet<string>();
        while (true)
        {
            var reply = answers.Ask("scene", $"Scene {scenes.Count + 1} as \"name: what it shows\" "
                                             + "(blank to finish)");
            if (reply.Trim().Length == 0)
                break;
            var (sceneName, label) = ParseScene(reply);
            if (!seen.Add(sceneName))
            {
                answers.Warn($"scene {PyRepr.Of(sceneName)} already exists; skipped");
                continue;
            }
            var narration = new Dictionary<string, string>();
            foreach (var lang in languages)
                narration.TryAdd(lang, "");
            scenes.Add(new Scene(sceneName, label, narration, new List<string> { "say" }));
        }
        if (scenes.Count == 0)
            throw new MissingAnswer("a demo needs at least one scene (--scene \"name: label\")");
        return new Profile(name, url, launch)
        {
            StorageState = storageState,
            Languages = languages,
            Scenes = scenes,
        };
    }

    public static readonly string Makefile = """
        # Generated by `tramoya init`. Three targets, one video.
        OUT ?= out
        LANG ?= {lang}

        .PHONY: take voices video all
        all: video

        take:
        	tramoya direct tramoya.toml --out $(OUT)

        voices: take
        {voices}
        video: voices
        	tramoya assemble --video $(OUT)/take.webm --marks $(OUT)/marks.json \
        	  --voice-dir $(OUT)/voices/$(LANG) --out $(OUT)/{slug}-$(LANG).mp4 --lang $(LANG)

        """.ReplaceLineEndings("\n");

    /// <summary>Write tramoya.toml, script.json and a Makefile into <paramref name="directory"/>.</summary>
    public static List<string> Scaffold(Profile profile, string directory, bool force = false)
    {
        Directory.CreateDirectory(directory);
        var voices = string.Join("\n", profile.Languages.Select(lang =>
            $"\ttramoya tts --script $(OUT)/script.json --lang {lang} --out-dir $(OUT)/voices/{lang}"));
        var targets = new List<(string Name, string Text)>
        {
            ("tramoya.toml", ProfileFile.Dump(profile)),
            // Python's json.dumps separates lines with "\n" whatever the platform; the
            // serializer follows Environment.NewLine, so the text is normalised here.
            ("script.json", Json.Dump(profile.Script(), indent: true, trailingNewline: true).ReplaceLineEndings("\n")),
            ("Makefile", Makefile
                .Replace("{voices}", voices)
                .Replace("{slug}", Slug(profile.Name))
                .Replace("{lang}", profile.Languages[0])),
        };
        var clashes = targets.Select(t => t.Name)
            .Where(n => File.Exists(Path.Combine(directory, n)) || Directory.Exists(Path.Combine(directory, n)))
            .ToList();
        if (clashes.Count > 0 && !force)
            throw new IOException($"{string.Join(", ", clashes)} already exist in {directory}; "
                                  + "pass --force to overwrite");
        var written = new List<string>();
        foreach (var (name, text) in targets)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            written.Add(path);
        }
        return written;
    }
}
