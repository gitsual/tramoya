using System.Text.Json.Nodes;
using Tramoya.Core;

namespace Tramoya.Tests;

/// <summary>
/// The assistant turns a plain-language request into a plan of tramoya commands, through
/// either a local Ollama model or the Claude API. Both backends are exercised with a fake
/// HTTP <c>post</c>; nothing talks to a server (port of <c>tests/test_assistant.py</c>).
/// </summary>
public class AssistantTests
{
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

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tramoya-assistant-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private sealed class FakePost
    {
        private readonly string _reply;
        public List<(string Url, JsonNode Payload, IDictionary<string, string> Headers)> Calls { get; } = new();
        public FakePost(string reply) => _reply = reply;
        public JsonNode Call(string url, JsonNode payload, IDictionary<string, string> headers)
        {
            Calls.Add((url, payload, headers));
            return JsonNode.Parse(_reply)!;
        }
    }

    private sealed class FakeBackend : IBackend
    {
        private readonly Queue<string> _replies;
        public List<(string System, string User)> Prompts { get; } = new();
        public FakeBackend(params string[] replies) => _replies = new Queue<string>(replies);
        public string Complete(string system, string user)
        {
            Prompts.Add((system, user));
            return _replies.Count > 1 ? _replies.Dequeue() : _replies.Peek();
        }
    }

    private static PlanStep S(string title, string why, params string[] command) => new(title, why, command.ToList());

    private static void AssertJsonEqual(string expected, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), $"expected {expected}, got {actual?.ToJsonString()}");

    // --- parsing ----------------------------------------------------------

    [Fact]
    public void ParsePlanAcceptsFencedJson()
    {
        var plan = Assistant.ParsePlan("Here you go:\n```json\n" + PlanJson + "\n```");
        Assert.StartsWith("Cut the take", plan.Summary);
        Assert.Equal(new[] { "Check the scene table", "Assemble the video" }, plan.Steps.Select(s => s.Title));
        Assert.Equal(new[] { "tramoya", "assemble" }, plan.Steps[1].Command.Take(2));
    }

    [Fact]
    public void ParsePlanRejectsGarbage()
    {
        var exc = Assert.Throws<ArgumentException>(() => Assistant.ParsePlan("I cannot help with that."));
        Assert.Contains("not a plan", exc.Message);
        Assert.Equal("the reply is not a plan: 'I cannot help with that.'", exc.Message);
    }

    [Fact]
    public void ParsePlanRejectsMissingSteps()
    {
        var exc = Assert.Throws<ArgumentException>(() => Assistant.ParsePlan("{\"summary\": \"x\"}"));
        Assert.Contains("steps", exc.Message);
    }

    // --- backends ---------------------------------------------------------

    [Fact]
    public void OllamaBackendPostsAJsonChat()
    {
        var post = new FakePost(new JsonObject
        {
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = PlanJson },
        }.ToJsonString());
        var backend = new OllamaBackend(model: "qwen3-coder", baseUrl: "http://localhost:11434", post: post.Call);
        var text = backend.Complete("SYSTEM", "USER");
        var (url, payload, _) = post.Calls[0];
        Assert.Equal("http://localhost:11434/api/chat", url);
        Assert.Equal("qwen3-coder", payload["model"]!.GetValue<string>());
        Assert.Equal("json", payload["format"]!.GetValue<string>());
        Assert.False(payload["stream"]!.GetValue<bool>());
        AssertJsonEqual("{\"role\": \"system\", \"content\": \"SYSTEM\"}", payload["messages"]![0]);
        AssertJsonEqual("{\"role\": \"user\", \"content\": \"USER\"}", payload["messages"]![1]);
        Assert.StartsWith("Cut", JsonNode.Parse(text)!["summary"]!.GetValue<string>());
    }

    [Fact]
    public void ClaudeBackendPostsAMessagesCall()
    {
        var post = new FakePost(new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = PlanJson }),
        }.ToJsonString());
        var backend = new ClaudeBackend(model: "claude-sonnet-5", apiKey: "sk-test", post: post.Call);
        var text = backend.Complete("SYSTEM", "USER");
        var (url, payload, headers) = post.Calls[0];
        Assert.Equal("https://api.anthropic.com/v1/messages", url);
        Assert.Equal("sk-test", headers["x-api-key"]);
        Assert.NotEmpty(headers["anthropic-version"]);
        Assert.Equal("claude-sonnet-5", payload["model"]!.GetValue<string>());
        Assert.Equal("SYSTEM", payload["system"]!.GetValue<string>());
        AssertJsonEqual("[{\"role\": \"user\", \"content\": \"USER\"}]", payload["messages"]);
        Assert.True(payload["max_tokens"]!.GetValue<int>() >= 1024);
        Assert.StartsWith("Cut", JsonNode.Parse(text)!["summary"]!.GetValue<string>());
    }

    private static Dictionary<string, string> Env(params (string, string)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void PickBackendPrefersTheExplicitNameThenEnvThenKey()
    {
        Assert.IsType<OllamaBackend>(Assistant.PickBackend("ollama", Env(("ANTHROPIC_API_KEY", "k"))));
        Assert.IsType<ClaudeBackend>(Assistant.PickBackend(null, Env(("TRAMOYA_LLM", "claude"), ("ANTHROPIC_API_KEY", "k"))));
        Assert.IsType<ClaudeBackend>(Assistant.PickBackend(null, Env(("ANTHROPIC_API_KEY", "k"))));
        Assert.IsType<OllamaBackend>(Assistant.PickBackend(null, Env()));
    }

    [Fact]
    public void PickBackendClaudeWithoutKeyIsAnError()
    {
        var exc = Assert.Throws<ArgumentException>(() => Assistant.PickBackend("claude", Env()));
        Assert.Contains("ANTHROPIC_API_KEY", exc.Message);
        var unknown = Assert.Throws<ArgumentException>(() => Assistant.PickBackend("gemini", Env()));
        Assert.Equal("unknown backend 'gemini': use 'ollama' or 'claude'", unknown.Message);
    }

    [Fact]
    public void PickBackendHonoursModelOverrides()
    {
        var backend = Assert.IsType<OllamaBackend>(Assistant.PickBackend("ollama", Env(("TRAMOYA_LLM_MODEL", "llama3.2:3b"))));
        Assert.Equal("llama3.2:3b", backend.Model);
        backend = Assert.IsType<OllamaBackend>(Assistant.PickBackend("ollama", Env(), model: "gpt-oss:20b"));
        Assert.Equal("gpt-oss:20b", backend.Model);
    }

    // --- ask / render / run -----------------------------------------------

    [Fact]
    public void AskSendsTheRequestAndTheFilesItCanSee()
    {
        var backend = new FakeBackend(PlanJson);
        var plan = Assistant.Ask(backend, "make the video with voice", files: new[] { "take.mp4", "marks.json" });
        var (system, user) = backend.Prompts[0];
        Assert.Contains("tramoya assemble", system);  // the model is told what the tool can do
        Assert.Contains("make the video with voice", user);
        Assert.Contains("take.mp4", user);
        Assert.Contains("marks.json", user);
        Assert.IsType<Plan>(plan);
    }

    [Fact]
    public void RenderPlanIsReadableAtBothLevels()
    {
        var plan = Assistant.ParsePlan(PlanJson);
        var text = Assistant.RenderPlan(plan);
        Assert.Contains("Cut the take by its marks", text);
        Assert.Contains("1. Check the scene table", text);
        Assert.Contains("why: See what the marks say", text);
        Assert.Contains("$ tramoya marks marks.json", text);
    }

    [Fact]
    public void RunPlanCallsEachTramoyaCommandInOrder()
    {
        var plan = Assistant.ParsePlan(PlanJson);
        var seen = new List<string[]>();
        var codes = Assistant.RunPlan(plan, runner: argv => { seen.Add(argv); return 0; });
        Assert.Equal(new[]
        {
            new[] { "marks", "marks.json" },
            new[] { "assemble", "--video", "take.mp4", "--marks", "marks.json", "--voice-dir", "voices/en", "--out", "demo.mp4" },
        }, seen);
        Assert.Equal(new[] { 0, 0 }, codes);
    }

    [Fact]
    public void RunPlanStopsAtTheFirstFailure()
    {
        var plan = Assistant.ParsePlan(PlanJson);
        var codes = Assistant.RunPlan(plan, runner: _ => 3);
        Assert.Equal(new[] { 3 }, codes);
    }

    [Fact]
    public void RunPlanRefusesAnythingThatIsNotTramoya()
    {
        var plan = new Plan("x", new List<PlanStep> { S("rm", "no", "rm", "-rf", "/") });
        var exc = Assert.Throws<ArgumentException>(() => Assistant.RunPlan(plan, runner: _ => 0));
        Assert.Contains("only tramoya commands", exc.Message);
        Assert.Equal("only tramoya commands can run, not ['rm', '-rf', '/']", exc.Message);
    }

    [Fact]
    public void RunPlanDryRunAddsTheFlagWhereItExists()
    {
        var plan = Assistant.ParsePlan(PlanJson);
        var seen = new List<string[]>();
        Assistant.RunPlan(plan, runner: argv => { seen.Add(argv); return 0; }, dryRun: true);
        Assert.Equal(new[] { "marks", "marks.json" }, seen[0]);  // `marks` has no --dry-run
        Assert.Equal("--dry-run", seen[1][^1]);
    }

    // The Python file's CLI cases (ask, do --yes, do without --yes, do refusing a plan
    // that fails preflight) are in CliTests.cs, where the CLI lives.

    // --- preflight --------------------------------------------------------

    [Fact]
    public void PreflightReportsInputsThatDoNotExist()
    {
        var plan = Assistant.ParsePlan(PlanJson);
        var problems = Assistant.Preflight(plan, exists: p => p is "take.mp4" or "marks.json");
        Assert.Equal(new[] { "step 2 needs voices/en, which does not exist" }, problems);
    }

    [Fact]
    public void PreflightAcceptsOutputsOfEarlierSteps()
    {
        var plan = new Plan("", new List<PlanStep>
        {
            S("bed", "", "tramoya", "music", "bed", "--out", "bed.wav"),
            S("mix", "", "tramoya", "music", "mix", "--video", "demo.mp4", "--bed", "bed.wav", "--out", "final.mp4"),
        });
        Assert.Empty(Assistant.Preflight(plan, exists: p => p == "demo.mp4"));
    }

    [Fact]
    public void PreflightFlagsPlaceholdersInCapitals()
    {
        var plan = new Plan("", new List<PlanStep> { S("m", "", "tramoya", "marks", "MARKS.json") });
        var problems = Assistant.Preflight(plan, exists: _ => false);
        Assert.Equal(new[] { "step 1 needs MARKS.json, which does not exist" }, problems);
    }

    [Fact]
    public void PreflightFlagsALanguageWithoutAVoice()
    {
        using var tmp = new TempDir();
        var script = tmp.File("script.json");
        File.WriteAllText(script, "{\"scene-a\": {\"en\": \"hi\"}}");
        var plan = new Plan("", new List<PlanStep>
        {
            S("tts", "", "tramoya", "tts", "--script", script, "--lang", "fr", "--out-dir", "voices/fr"),
        });
        Assert.Equal(new[] { "step 1 asks for language fr, but voices exist only for en, es" }, Assistant.Preflight(plan));
    }

    [Fact]
    public void PreflightFlagsAScriptThatIsNotAScript()
    {
        using var tmp = new TempDir();
        var marks = tmp.File("marks.json");
        File.WriteAllText(marks, "[{\"t\": 0, \"key\": \"scene:a:start\", \"label\": \"\"}]");
        var plan = new Plan("", new List<PlanStep>
        {
            S("tts", "", "tramoya", "tts", "--script", marks, "--lang", "en", "--out-dir", "voices/en"),
        });
        Assert.Equal(new[]
        {
            $"step 1 uses {marks} as a narration script, but it is not one "
            + "(a script maps each scene key to its text per language)",
        }, Assistant.Preflight(plan));
    }

    [Fact]
    public void AskRepairsAPlanOnceWhenPreflightComplains()
    {
        var bad = PlanJson.Replace("marks.json", "MARKS.json");
        var backend = new FakeBackend(bad, PlanJson);
        var known = new HashSet<string> { "take.mp4", "marks.json", "voices/en" };
        var plan = Assistant.Ask(backend, "make the video", files: known.ToList(), exists: known.Contains);
        Assert.Equal(2, backend.Prompts.Count);
        Assert.Contains("MARKS.json, which does not exist", backend.Prompts[1].User);
        Assert.Equal(new[] { "tramoya", "marks", "marks.json" }, plan.Steps[0].Command);
    }

    [Fact]
    public void PreflightKnowsWhatDirectProduces()
    {
        // The Python test chdirs into tmp_path; here the script keeps its absolute path
        // because the working directory is shared by every test in the process.
        using var tmp = new TempDir();
        var script = tmp.File("script.json");
        File.WriteAllText(script, "{\"scene-welcome\": {\"en\": \"Hello\"}}");
        var plan = new Plan("", new List<PlanStep>
        {
            S("direct", "", "tramoya", "direct", "director.py", "--out", "out"),
            S("voice", "", "tramoya", "tts", "--script", script, "--lang", "en", "--out-dir", "out/voices/en"),
            S("build", "", "tramoya", "assemble", "--video", "out/take.webm", "--marks", "out/marks.json",
              "--voice-dir", "out/voices/en", "--out", "demo.mp4"),
        });
        Assert.Empty(Assistant.Preflight(plan, exists: p => p == "director.py" || p == script));
    }

    [Fact]
    public void PreflightReportsAMissingDirectorScript()
    {
        var plan = new Plan("", new List<PlanStep>
        {
            S("direct", "", "tramoya", "direct", "director.py", "--out", "out"),
        });
        Assert.Equal(new[] { "step 1 needs director.py, which does not exist" }, Assistant.Preflight(plan, exists: _ => false));
    }

    [Fact]
    public void RunPlanDryRunReachesDirect()
    {
        var plan = new Plan("", new List<PlanStep>
        {
            S("direct", "", "tramoya", "direct", "director.py", "--out", "out"),
        });
        var seen = new List<string[]>();
        Assistant.RunPlan(plan, runner: argv => { seen.Add(argv); return 0; }, dryRun: true);
        Assert.Equal(new[] { new[] { "direct", "director.py", "--out", "out", "--dry-run" } }, seen);
    }

    [Fact]
    public void PreflightReportsDirectWithoutAScript()
    {
        var plan = new Plan("", new List<PlanStep>
        {
            S("direct", "", "tramoya", "direct", "--out", "out", "--lang", "en"),
        });
        Assert.Equal(new[] { "step 1 does not say which director script to run (tramoya direct <director.py> ...)" },
            Assistant.Preflight(plan, exists: _ => true));
    }

    [Fact]
    public void AskTellsTheModelAboutTheProfile()
    {
        var profile = new Profile("Notes", url: "http://x/")
        {
            Languages = new List<string> { "en", "es" },
            Scenes = new List<Scene>
            {
                new("welcome", "A small notes app", new Dictionary<string, string> { ["en"] = "Hi" }, new List<string> { "say" }),
            },
        };
        var reply = "{\"summary\": \"ok\", \"steps\": []}";
        var post = new FakePost(new JsonObject { ["message"] = new JsonObject { ["content"] = reply } }.ToJsonString());
        var backend = new OllamaBackend(post: post.Call);
        Assistant.Ask(backend, "make the demo", new[] { "tramoya.toml" }, profile: profile);
        var user = post.Calls[0].Payload["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("Notes", user);
        Assert.Contains("welcome", user);
        Assert.Contains("tramoya.toml", user);
        Assert.Contains("en, es", user);
        var system = post.Calls[0].Payload["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("tramoya direct tramoya.toml", system);
        Assert.Equal(
            "Files in the working directory:\n  tramoya.toml\n\n"
            + "App profile (tramoya.toml): Notes at http://x/\n  narration languages: en, es\n  scenes:\n    welcome: A small notes app\n\n"
            + "Request: make the demo", user);
    }

    [Fact]
    public void PreflightAcceptsDirectWithAProfileAndItsOutputs()
    {
        var plan = new Plan("x", new List<PlanStep>
        {
            S("take", "why", "tramoya", "direct", "tramoya.toml", "--out", "out"),
            S("voice", "why", "tramoya", "tts", "--script", "out/script.json", "--lang", "en", "--out-dir", "out/voices/en"),
            S("cut", "why", "tramoya", "assemble", "--video", "out/take.webm", "--marks", "out/marks.json",
              "--voice-dir", "out/voices/en", "--out", "d.mp4"),
        });
        Assert.Empty(Assistant.Preflight(plan, exists: p => p == "tramoya.toml"));
    }
}
