using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tramoya.Core;

/// <summary>
/// Plain language in, a plan of tramoya commands out (port of <c>tramoya/assistant.py</c>).
///
/// <see cref="Assistant.Ask"/> sends a request such as "make the video with voice" to a
/// language model together with a description of what the command line can do and the
/// files in the working directory, and gets back a <see cref="Plan"/>: a one-line summary
/// (the high level) and numbered steps, each with a reason and the exact <c>tramoya</c>
/// command it runs (the low level).
///
/// Two backends, same contract: <see cref="OllamaBackend"/> for a local model and
/// <see cref="ClaudeBackend"/> for the Claude API. Both take an injectable <c>post</c> so
/// tests never open a socket. <see cref="Assistant.RunPlan"/> only ever executes
/// <c>tramoya</c> commands.
/// </summary>
public sealed record PlanStep(string Title, string Why, List<string> Command);

public sealed record Plan(string Summary, List<PlanStep> Steps)
{
    public Plan(string summary) : this(summary, new List<PlanStep>()) { }
}

public interface IBackend
{
    string Complete(string system, string user);
}

/// <summary>
/// A model served by Ollama, asked through its native chat endpoint with <c>format: json</c>
/// so the reply is a JSON object.
/// </summary>
public sealed class OllamaBackend : IBackend
{
    public string Model { get; }
    public string BaseUrl { get; }
    private readonly Func<string, JsonNode, IDictionary<string, string>, JsonNode> _post;

    public OllamaBackend(
        string model = Assistant.DefaultOllamaModel,
        string baseUrl = Assistant.DefaultOllamaUrl,
        Func<string, JsonNode, IDictionary<string, string>, JsonNode>? post = null)
    {
        Model = model;
        BaseUrl = baseUrl.TrimEnd('/');
        _post = post ?? Assistant.HttpPost;
    }

    public string Complete(string system, string user)
    {
        var reply = _post(
            $"{BaseUrl}/api/chat",
            new JsonObject
            {
                ["model"] = Model,
                ["format"] = "json",
                ["stream"] = false,
                ["options"] = new JsonObject { ["temperature"] = 0 },
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = system },
                    new JsonObject { ["role"] = "user", ["content"] = user },
                },
            },
            new Dictionary<string, string>());
        return Assistant.Key(Assistant.Key(reply, "message"), "content").GetValue<string>();
    }
}

/// <summary>The Claude Messages API.</summary>
public sealed class ClaudeBackend : IBackend
{
    public string Model { get; }
    public string ApiKey { get; }
    private readonly Func<string, JsonNode, IDictionary<string, string>, JsonNode> _post;

    public ClaudeBackend(
        string apiKey,
        string model = Assistant.DefaultClaudeModel,
        Func<string, JsonNode, IDictionary<string, string>, JsonNode>? post = null)
    {
        Model = model;
        ApiKey = apiKey;
        _post = post ?? Assistant.HttpPost;
    }

    public string Complete(string system, string user)
    {
        var reply = _post(
            Assistant.ClaudeUrl,
            new JsonObject
            {
                ["model"] = Model,
                ["max_tokens"] = 2048,
                ["system"] = system,
                ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = user } },
            },
            new Dictionary<string, string> { ["x-api-key"] = ApiKey, ["anthropic-version"] = "2023-06-01" });
        var sb = new StringBuilder();
        foreach (var block in Assistant.Key(reply, "content").AsArray())
            if (block is JsonObject o && o["text"] is JsonNode text)
                sb.Append(text.GetValue<string>());
        return sb.ToString();
    }
}

public static class Assistant
{
    public const string DefaultOllamaModel = "qwen3-coder:30b";
    public const string DefaultOllamaUrl = "http://localhost:11434";
    public const string DefaultClaudeModel = "claude-sonnet-5-5";
    public const string ClaudeUrl = "https://api.anthropic.com/v1/messages";

    /// <summary>Subcommands that accept <c>--dry-run</c>; <see cref="RunPlan"/> appends it there.</summary>
    public static readonly IReadOnlySet<string> DryRunCapable =
        new HashSet<string> { "assemble", "deck", "captions", "music", "record", "direct" };

    /// <summary>The languages <c>tramoya tts</c> has a voice for (Python: <c>tramoya.tts.VOICES</c>).</summary>
    public static readonly IReadOnlySet<string> Voices = new HashSet<string> { "es", "en" };

    public static readonly string SystemPrompt = """
You are the assistant of `tramoya`, a command-line tool that turns a
screen recording of a software demo into a finished video. The user is not a
video engineer. Translate what they ask into a short plan of `tramoya`
commands. Never invent files: use only the files listed, or names the user
gave, or outputs of earlier steps.

Commands you may use (and nothing else):
  tramoya marks <marks.json>                      print the scene table of a marks file
  tramoya assemble --video <take.mp4> --marks <marks.json> --voice-dir <voices-dir>
                   --out <output> [--lang en]   cut the take per scene, fit the voice, join
  tramoya tts --script <script.json> --lang <en|es> --out-dir <dir>
                                                synthesize one voice clip per script key
  tramoya deck --slides <slides-dir> --voice-dir <voices-dir> --out <output>
               [--clip <video> --clip-duration <seconds>]
                                                slide PNGs + narration (+ a clip) -> video
  tramoya captions burn --video <input> --subtitles <file.srt> --out <output> [--trim 0-120,600-780]
  tramoya music bed --out bed.wav               synthesize a looping background bed
  tramoya music mix --video <input> --bed bed.wav --out <output> [--solo]
  tramoya cues marks --cues <cues-file> --out <marks.json> [--meta <meta-file> --duration <seconds>]
  tramoya cues ass --cues <cues-file> --duration <seconds> --width <w> --height <h> --out <file.ass>
  tramoya record --out <take.mp4> --geometry "<x>,<y> <w>x<h>" [--fps <n>]
  tramoya direct <director.py> --out <dir> [--pace <factor>] [--rehearse] [--lang <en|es>]
                                                run a director script (a Python file that
                                                drives the app on stage and records it);
                                                writes <dir>/take.webm and <dir>/marks.json
  tramoya direct tramoya.toml --out <dir> [--pace <factor>] [--rehearse] [--lang <en|es>]
                                                run the app profile: every scene in
                                                tramoya.toml; writes <dir>/take.webm,
                                                <dir>/marks.json and <dir>/script.json
                                                (the narration script for tts)

Rules:
- `direct` takes the director script or the profile as its first argument,
  before any flag: `tramoya direct director.py --out out` or
  `tramoya direct tramoya.toml --out out`. It records the app itself; do not
  add a `record` step for it. When an app profile is described below, prefer
  `direct tramoya.toml` and then `tts --script <dir>/script.json`.
- `tts` needs a narration script JSON (key -> {lang: text}); `marks.json` is
  not a script. Voices exist for "en" and "es" only.
- Anything in <angle brackets> above is a placeholder: replace it with a real
  file name from the list, or a name the user gave, or an output of an
  earlier step. If a needed input is not listed, return no steps and say in
  the summary which file is missing.

Answer with JSON only, in this exact shape:
{"summary": "one sentence saying what will happen and why",
 "steps": [{"title": "short imperative title",
            "why": "one sentence a non-technical person understands",
            "command": ["tramoya", "assemble", "--video", "take.mp4", "..."]}]}
Every command is a list of strings starting with "tramoya". Prefer the fewest
steps that do the job. When the request is impossible with these commands,
return a plan with no steps and say why in the summary.
""".ReplaceLineEndings("\n");

    // --- transport ------------------------------------------------------

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(300) };

    /// <summary>POST JSON, return the decoded JSON body. The only network call here.</summary>
    public static JsonNode HttpPost(string url, JsonNode payload, IDictionary<string, string> headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(Json.Compact), Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        foreach (var (name, value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content.Headers.TryAddWithoutValidation(name, value);
        }
        using var response = Http.Send(request);
        response.EnsureSuccessStatusCode();
        using var stream = response.Content.ReadAsStream();
        return JsonNode.Parse(stream) ?? throw new InvalidOperationException("empty JSON reply");
    }

    /// <summary>Python's <c>reply[key]</c>: a missing key is a <c>KeyError</c>.</summary>
    internal static JsonNode Key(JsonNode node, string key) =>
        node is JsonObject o && o[key] is JsonNode value ? value : throw new KeyNotFoundException(PyRepr.Of(key));

    /// <summary>
    /// Choose a backend: the explicit <paramref name="name"/>, else <c>$TRAMOYA_LLM</c>, else
    /// Claude when <c>$ANTHROPIC_API_KEY</c> is set, else a local Ollama model.
    ///
    /// The model comes from <paramref name="model"/>, else <c>$TRAMOYA_LLM_MODEL</c>, else a default.
    /// </summary>
    public static IBackend PickBackend(string? name, IDictionary<string, string>? env = null, string? model = null)
    {
        env ??= Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
        string? Get(string key) => env.TryGetValue(key, out var v) ? v : null;
        var fallback = !string.IsNullOrEmpty(Get("ANTHROPIC_API_KEY")) ? "claude" : "ollama";
        var chosen = FirstNonEmpty(name, Get("TRAMOYA_LLM")) ?? fallback;
        model = FirstNonEmpty(model, Get("TRAMOYA_LLM_MODEL"));
        if (chosen == "claude")
        {
            var key = Get("ANTHROPIC_API_KEY") ?? "";
            if (key.Length == 0)
                throw new ArgumentException("the claude backend needs ANTHROPIC_API_KEY in the environment");
            return new ClaudeBackend(apiKey: key, model: model ?? DefaultClaudeModel);
        }
        if (chosen == "ollama")
        {
            return new OllamaBackend(
                model: model ?? DefaultOllamaModel,
                baseUrl: Get("OLLAMA_HOST") ?? DefaultOllamaUrl);
        }
        throw new ArgumentException($"unknown backend {PyRepr.Of(chosen)}: use 'ollama' or 'claude'");
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrEmpty(v));

    // --- plans ----------------------------------------------------------

    private static readonly Regex Fence = new("```(?:json)?\\s*(.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Read a <see cref="Plan"/> out of a model reply, with or without a code fence.</summary>
    public static Plan ParsePlan(string text)
    {
        var match = Fence.Match(text);
        var body = match.Success ? match.Groups[1].Value : text;
        int start = body.IndexOf('{'), end = body.LastIndexOf('}');
        if (start < 0 || end < 0)
            throw new ArgumentException($"the reply is not a plan: {PyRepr.Of(text.Length > 80 ? text[..80] : text)}");
        JsonNode? raw;
        try
        {
            raw = JsonNode.Parse(body.Substring(start, end + 1 - start));
        }
        catch (JsonException exc)
        {
            throw new ArgumentException($"the reply is not a plan: {exc.Message}", exc);
        }
        if (raw is not JsonObject obj || obj["steps"] is not JsonArray stepsRaw)
            throw new ArgumentException("the reply has no steps");
        var steps = new List<PlanStep>();
        foreach (var s in stepsRaw)
        {
            if (s is not JsonObject step)
                throw new ArgumentException("the reply has no steps");
            var command = step["command"] is JsonArray parts
                ? parts.Select(part => Str(part)).ToList()
                : new List<string>();
            steps.Add(new PlanStep(Str(step["title"], ""), Str(step["why"], ""), command));
        }
        return new Plan(Str(obj["summary"], ""), steps);
    }

    /// <summary>Python's <c>str()</c> over a JSON value, with <paramref name="default"/> for a missing key.</summary>
    private static string Str(JsonNode? node, string? @default = null)
    {
        if (node is null) return @default ?? "None";
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s)) return s;
            if (value.TryGetValue<bool>(out var b)) return b ? "True" : "False";
            if (value.TryGetValue<double>(out var d) && !double.IsInteger(d)) return Fmt.Py(d);
        }
        return node.ToJsonString(Json.Compact);
    }

    /// <summary>The app profile for the model: name, where it runs, languages, scenes.</summary>
    public static string DescribeProfile(Profile profile)
    {
        var where = FirstNonEmpty(profile.Url, profile.Ready) ?? "?";
        var lines = new List<string>
        {
            $"App profile (tramoya.toml): {profile.Name} at {where}",
            $"  narration languages: {string.Join(", ", profile.Languages)}",
            "  scenes:",
        };
        lines.AddRange(profile.Scenes.Select(s => $"    {s.Name}: {s.Label}"));
        return string.Join("\n", lines);
    }

    public static string DescribeFiles(IList<string> files)
    {
        if (files.Count == 0)
            return "The working directory is empty.";
        return "Files in the working directory:\n" + string.Join("\n", files.Select(f => $"  {f}"));
    }

    /// <summary>Python's <c>os.path.exists</c>.</summary>
    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Turn <paramref name="request"/> into a <see cref="Plan"/>, telling the model which files exist.
    ///
    /// When the plan would fail <see cref="Preflight"/>, the problems are sent back to the
    /// model once (<paramref name="repairs"/> times) so it can fix its own mistakes,
    /// typically a placeholder file name. The caller still runs <see cref="Preflight"/>
    /// before running.
    /// </summary>
    public static Plan Ask(
        IBackend backend,
        string request,
        IList<string> files,
        Func<string, bool>? exists = null,
        int repairs = 1,
        Profile? profile = null)
    {
        exists ??= PathExists;
        var context = DescribeFiles(files);
        if (profile is not null)
            context += "\n\n" + DescribeProfile(profile);
        var user = $"{context}\n\nRequest: {request}";
        var plan = ParsePlan(backend.Complete(SystemPrompt, user));
        for (var i = 0; i < repairs; i++)
        {
            var problems = Preflight(plan, exists);
            if (problems.Count == 0)
                break;
            user += "\n\nYour previous plan cannot run:\n"
                    + string.Join("\n", problems.Select(p => $"  {p}"))
                    + "\nReturn a corrected plan using only the files listed above, "
                    + "or no steps if the request cannot be met.";
            plan = ParsePlan(backend.Complete(SystemPrompt, user));
        }
        return plan;
    }

    /// <summary>
    /// The plan for a person: what will happen, then each step at both levels (why in
    /// words, the exact command underneath).
    /// </summary>
    public static string RenderPlan(Plan plan)
    {
        var lines = new List<string> { plan.Summary, "" };
        if (plan.Steps.Count == 0)
            lines.Add("(nothing to run)");
        var i = 0;
        foreach (var step in plan.Steps)
        {
            i++;
            lines.Add($"{i}. {step.Title}");
            lines.Add($"   why: {step.Why}");
            lines.Add($"   $ {string.Join(" ", step.Command)}");
        }
        return string.Join("\n", lines);
    }

    // Flags whose value must be a file or directory that already exists.
    private static readonly HashSet<string> InputFlags = new()
    {
        "--video", "--marks", "--voice-dir", "--script", "--slides", "--clip",
        "--subtitles", "--bed", "--cues", "--meta",
    };
    private static readonly HashSet<string> OutputFlags = new() { "--out", "--out-dir" };

    private static List<string> InputsOf(List<string> argv)
    {
        var inputs = new List<string>();
        for (var i = 0; i < argv.Count - 1; i++)
            if (InputFlags.Contains(argv[i]))
                inputs.Add(argv[i + 1]);
        if (argv.Count > 1 && (argv[0] == "marks" || argv[0] == "direct") && !argv[1].StartsWith('-'))
            inputs.Add(argv[1]);
        return inputs;
    }

    private static HashSet<string> OutputsOf(List<string> argv)
    {
        var outputs = new HashSet<string>();
        for (var j = 0; j < argv.Count - 1; j++)
            if (OutputFlags.Contains(argv[j]))
                outputs.Add(argv[j + 1]);
        if (argv.Count > 0 && argv[0] == "direct")
        {
            var @out = FlagValue(argv, "--out");
            if (!string.IsNullOrEmpty(@out))
                outputs.UnionWith(new[] { $"{@out}/take.webm", $"{@out}/marks.json", $"{@out}/script.json", $"{@out}/snapshots" });
        }
        return outputs;
    }

    private static string? FlagValue(List<string> argv, string flag)
    {
        for (var i = 0; i < argv.Count - 1; i++)
            if (argv[i] == flag)
                return argv[i + 1];
        return null;
    }

    private static bool IsScript(string path)
    {
        JsonNode? raw;
        try
        {
            raw = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                  or ArgumentException or NotSupportedException)
        {
            return false;
        }
        return raw is JsonObject obj && obj.All(kv => kv.Value is JsonObject);
    }

    /// <summary>
    /// Say, in plain words, what would make a step fail before anything runs: an input
    /// that does not exist, a language with no voice, a file passed as a narration script
    /// that is not one. An input produced by an earlier step's <c>--out</c>/<c>--out-dir</c>
    /// counts as present.
    /// </summary>
    public static List<string> Preflight(Plan plan, Func<string, bool>? exists = null)
    {
        exists ??= PathExists;
        var produced = new HashSet<string>();
        var problems = new List<string>();
        var i = 0;
        foreach (var step in plan.Steps)
        {
            i++;
            var argv = step.Command.Skip(1).ToList();
            if (argv.Count > 0 && argv[0] == "direct" && (argv.Count < 2 || argv[1].StartsWith('-')))
                problems.Add($"step {i} does not say which director script to run "
                             + "(tramoya direct <director.py> ...)");
            foreach (var path in InputsOf(argv))
                if (!produced.Contains(path) && !exists(path))
                    problems.Add($"step {i} needs {path}, which does not exist");
            if (argv.Count > 0 && argv[0] == "tts")
            {
                var lang = FlagValue(argv, "--lang");
                if (!string.IsNullOrEmpty(lang) && !Voices.Contains(lang))
                {
                    var langs = string.Join(", ", Voices.Order(StringComparer.Ordinal));
                    problems.Add($"step {i} asks for language {lang}, but voices exist only for {langs}");
                }
                var script = FlagValue(argv, "--script");
                if (!string.IsNullOrEmpty(script) && exists(script) && !IsScript(script))
                    problems.Add($"step {i} uses {script} as a narration script, but it is not one "
                                 + "(a script maps each scene key to its text per language)");
            }
            produced.UnionWith(OutputsOf(argv));
        }
        return problems;
    }

    private static string[] ArgvFor(PlanStep step, bool dryRun)
    {
        if (step.Command.Count == 0 || step.Command[0] != "tramoya")
            throw new ArgumentException($"only tramoya commands can run, not {PyRepr.Of(step.Command)}");
        var argv = step.Command.Skip(1).ToList();
        if (dryRun && argv.Count > 0 && DryRunCapable.Contains(argv[0]) && !argv.Contains("--dry-run"))
            argv.Add("--dry-run");
        return argv.ToArray();
    }

    /// <summary>
    /// Run each step through <paramref name="runner"/> (the CLI's <c>main</c>), stopping at
    /// the first non-zero exit. Refuses any command that is not <c>tramoya</c>.
    /// </summary>
    public static List<int> RunPlan(Plan plan, Func<string[], int> runner, bool dryRun = false)
    {
        // Validate every step before running any.
        var argvs = plan.Steps.Select(step => ArgvFor(step, dryRun)).ToList();
        var codes = new List<int>();
        foreach (var argv in argvs)
        {
            var code = runner(argv);
            codes.Add(code);
            if (code != 0)
                break;
        }
        return codes;
    }
}
