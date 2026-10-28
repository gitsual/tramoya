namespace Tramoya.Core;

/// <summary>
/// The data of <c>tramoya.toml</c> (port of the dataclasses in <c>tramoya/profile.py</c>).
/// Reading, writing and the step language live in <c>ProfileFile</c> and <c>Steps</c>.
/// </summary>
public sealed class Scene
{
    public string Name { get; set; }
    public string Label { get; set; }
    /// <summary>lang -> narration text; insertion order is preserved when dumping.</summary>
    public Dictionary<string, string> Narration { get; set; }
    public List<string> Steps { get; set; }

    public Scene(string name, string label, Dictionary<string, string>? narration = null, List<string>? steps = null)
    {
        Name = name;
        Label = label;
        Narration = narration ?? new Dictionary<string, string>();
        Steps = steps ?? new List<string>();
    }
}

public sealed class Profile
{
    public static readonly (int Width, int Height) DefaultViewport = (1280, 720);

    public string Name { get; set; }
    public string? Url { get; set; }
    public string? Launch { get; set; }
    public string? Ready { get; set; }
    public (int Width, int Height) Viewport { get; set; } = DefaultViewport;
    public string? StorageState { get; set; }
    public List<string> Languages { get; set; } = new() { "en" };
    public double Pace { get; set; } = 1.0;
    public List<Scene> Scenes { get; set; } = new();
    /// <summary>The directory the profile lives in (Python: <c>root: Path</c>, default <c>Path()</c> = ".").</summary>
    public string Root { get; set; } = ".";

    public Profile(string name, string? url = null, string? launch = null, string? root = null)
    {
        Name = name;
        Url = url;
        Launch = launch;
        if (root is not null) Root = root;
    }

    /// <summary>
    /// The narration script <c>tramoya tts</c> reads: <c>scene-&lt;name&gt;</c> -> {lang: text}.
    /// Two scenes with the same name are a dict comprehension in Python: the last one wins
    /// and the key keeps its first position. A profile Python runs must not fail here.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> Script()
    {
        var script = new Dictionary<string, Dictionary<string, string>>();
        foreach (var scene in Scenes)
            script[$"scene-{scene.Name}"] = new Dictionary<string, string>(scene.Narration);
        return script;
    }
}
