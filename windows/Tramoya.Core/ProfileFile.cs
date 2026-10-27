using System.Globalization;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace Tramoya.Core;

/// <summary>
/// Reading and writing <c>tramoya.toml</c> (port of <c>load_profile</c>/<c>dump_profile</c>
/// in <c>tramoya/profile.py</c>).
///
/// One file names the application, how to reach it, the narration languages and the
/// scenes. Reading uses Tomlyn; writing is done by hand, exactly as the Python version
/// does, because the shape is small and both implementations must produce the same text.
/// </summary>
public static class ProfileFile
{
    public static Profile Load(string path)
    {
        TomlTable raw;
        try
        {
            raw = Toml.ToModel(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (TomlException ex)
        {
            // Python's TOMLDecodeError is a ValueError.
            throw new ArgumentException(ex.Message, ex);
        }
        var app = Table(raw, "app");
        var demo = Table(raw, "demo");
        var url = StrOrNull(app, "url");
        var launch = StrOrNull(app, "launch");
        if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(launch))
            throw new ArgumentException($"{path}: [app] needs a url, or a launch command with a ready url");
        var ready = StrOrNull(app, "ready");
        if (!string.IsNullOrEmpty(launch) && string.IsNullOrEmpty(url) && string.IsNullOrEmpty(ready))
            throw new ArgumentException($"{path}: [app] launch needs a url or a ready url to wait for");

        var viewport = app.TryGetValue("viewport", out var vp) && vp is TomlArray arr
            ? arr
            : new TomlArray { (long)Profile.DefaultViewport.Width, (long)Profile.DefaultViewport.Height };

        var scenes = new List<Scene>();
        if (raw.TryGetValue("scene", out var sceneList))
        {
            // tomllib hands both spellings over as a list of dicts; Tomlyn distinguishes
            // `[[scene]]` (TomlTableArray) from `scene = [{...}, {...}]` (TomlArray of tables).
            var tables = sceneList switch
            {
                TomlTableArray tableArray => tableArray.AsEnumerable(),
                TomlArray array => array.OfType<TomlTable>(),
                _ => Enumerable.Empty<TomlTable>(),
            };
            foreach (var s in tables)
            {
                if (!s.TryGetValue("name", out var nameValue))
                    throw new KeyNotFoundException("'name'");
                var name = Str(nameValue);
                var narration = new Dictionary<string, string>();
                foreach (var (k, v) in Table(s, "narration"))
                    narration[k] = Str(v);
                var steps = new List<string>();
                if (s.TryGetValue("steps", out var stepList) && stepList is TomlArray stepArr)
                    foreach (var line in stepArr)
                        steps.Add(Str(line));
                scenes.Add(new Scene(
                    name,
                    s.TryGetValue("label", out var label) ? Str(label) : name,
                    narration,
                    steps));
            }
        }

        var directory = Path.GetDirectoryName(path) ?? "";
        var languages = demo.TryGetValue("languages", out var langs) && langs is TomlArray langArr
            ? langArr.Select(Str).ToList()
            : new List<string> { "en" };
        return new Profile(
            app.TryGetValue("name", out var appName) ? Str(appName) : DefaultName(directory),
            url,
            launch,
            directory.Length == 0 ? "." : directory)
        {
            Ready = ready,
            Viewport = (Int(viewport[0]), Int(viewport[1])),
            StorageState = StrOrNull(app, "storage_state"),
            Languages = languages,
            Pace = demo.TryGetValue("pace", out var pace) ? Float(pace) : 1.0,
            Scenes = scenes,
        };
    }

    /// <summary>Python's <c>path.parent.name or "app"</c>.</summary>
    private static string DefaultName(string directory)
    {
        if (directory.Length == 0) return "app";
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        return name.Length == 0 || name == "." ? "app" : name;
    }

    private static TomlTable Table(TomlTable parent, string key) =>
        parent.TryGetValue(key, out var value) && value is TomlTable table ? table : new TomlTable();

    private static string? StrOrNull(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is not null ? Str(value) : null;

    /// <summary>Python's <c>str()</c> over the values Tomlyn produces.</summary>
    private static string Str(object? value) => value switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => Fmt.Py(d),
        _ => value.ToString() ?? "",
    };

    private static int Int(object? value) => value switch
    {
        long l => checked((int)l),
        double d => checked((int)Math.Truncate(d)),
        bool b => b ? 1 : 0,
        string s => int.Parse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"int() argument must be a string or a number, not '{value?.GetType().Name}'"),
    };

    private static double Float(object? value) => value switch
    {
        long l => l,
        double d => d,
        bool b => b ? 1.0 : 0.0,
        string s => double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"float() argument must be a string or a number, not '{value?.GetType().Name}'"),
    };

    // --- writing --------------------------------------------------------

    /// <summary>A TOML basic string; the same minimal quoting as the Python <c>_q</c>.</summary>
    private static string Q(string text) =>
        "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static string Dump(Profile profile)
    {
        var lines = new List<string> { "[app]", $"name = {Q(profile.Name)}" };
        if (!string.IsNullOrEmpty(profile.Url))
            lines.Add($"url = {Q(profile.Url)}");
        if (!string.IsNullOrEmpty(profile.Launch))
            lines.Add($"launch = {Q(profile.Launch)}");
        if (!string.IsNullOrEmpty(profile.Ready))
            lines.Add($"ready = {Q(profile.Ready)}");
        lines.Add($"viewport = [{profile.Viewport.Width.ToString(CultureInfo.InvariantCulture)}, {profile.Viewport.Height.ToString(CultureInfo.InvariantCulture)}]");
        if (!string.IsNullOrEmpty(profile.StorageState))
            lines.Add($"storage_state = {Q(profile.StorageState)}");
        lines.Add("");
        lines.Add("[demo]");
        lines.Add("languages = [" + string.Join(", ", profile.Languages.Select(Q)) + "]");
        lines.Add($"pace = {Fmt.Py(profile.Pace)}");
        foreach (var scene in profile.Scenes)
        {
            lines.Add("");
            lines.Add("[[scene]]");
            lines.Add($"name = {Q(scene.Name)}");
            lines.Add($"label = {Q(scene.Label)}");
            lines.Add("steps = [" + string.Join(", ", scene.Steps.Select(Q)) + "]");
            if (scene.Narration.Count > 0)
            {
                lines.Add("");
                lines.Add("[scene.narration]");
                foreach (var (lang, text) in scene.Narration)
                    lines.Add($"{lang} = {Q(text)}");
            }
        }
        return string.Join("\n", lines) + "\n";
    }
}
