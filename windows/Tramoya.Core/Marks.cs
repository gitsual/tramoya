using System.Text.Json.Nodes;

namespace Tramoya.Core;

/// <summary>A named moment of a take: <c>scene:&lt;name&gt;:start</c>, <c>wait:end</c>, ...</summary>
public sealed record Mark(double T, string Key, string Label = "");

public enum MarkKind { Scene, Wait, Other }

/// <summary>Scene and wait marks for a recorded demo run (port of <c>tramoya/marks.py</c>).</summary>
public static class MarkKeys
{
    public static string SceneKey(string name, string edge) => $"scene:{name}:{edge}";
    public static string WaitKey(string edge) => $"wait:{edge}";

    /// <summary>
    /// Parse a key into (kind, name, edge). Accepts the canonical keys and the legacy
    /// Spanish ones (<c>escena-&lt;name&gt;-inicio</c>, <c>espera-fin</c>, ...).
    /// </summary>
    public static (MarkKind Kind, string? Name, string? Edge) ParseKey(string key)
    {
        if (key.StartsWith("scene:", StringComparison.Ordinal))
        {
            var parts = key.Split(':', 3);
            return (MarkKind.Scene, parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "");
        }
        if (key.StartsWith("wait:", StringComparison.Ordinal))
            return (MarkKind.Wait, null, key["wait:".Length..]);
        if (key.StartsWith("escena-", StringComparison.Ordinal) && key.EndsWith("-inicio", StringComparison.Ordinal))
            return (MarkKind.Scene, key["escena-".Length..^"-inicio".Length], "start");
        if (key.StartsWith("escena-", StringComparison.Ordinal) && key.EndsWith("-fin", StringComparison.Ordinal))
            return (MarkKind.Scene, key["escena-".Length..^"-fin".Length], "end");
        if (key == "espera-inicio") return (MarkKind.Wait, null, "start");
        if (key == "espera-fin") return (MarkKind.Wait, null, "end");
        return (MarkKind.Other, key, null);
    }

    public static Mark FromRow(JsonNode row, double offset)
    {
        var obj = row.AsObject();
        string key, label;
        if (obj.ContainsKey("key"))
        {
            key = obj["key"]!.GetValue<string>();
            label = obj["label"]?.GetValue<string>() ?? "";
        }
        else
        {
            key = obj["clave"]!.GetValue<string>();
            label = obj["etiqueta"]?.GetValue<string>() ?? "";
        }
        return new Mark(obj["t"]!.GetValue<double>() + offset, key, label);
    }

    /// <summary>Load marks from a JSON file, accepting both the new and the legacy shapes.</summary>
    public static List<Mark> LoadMarks(string path, double offset = 0.0)
    {
        var raw = Json.LoadNode(path)!.AsArray();
        return raw.Select(row => FromRow(row!, offset)).ToList();
    }

    /// <summary>Each fully bounded scene name to (start, end), in first-seen order.</summary>
    public static List<KeyValuePair<string, (double Start, double End)>> SceneBounds(IEnumerable<Mark> marks)
    {
        var starts = new Dictionary<string, double>();
        var ends = new Dictionary<string, double>();
        var order = new List<string>();
        foreach (var mark in marks)
        {
            var (kind, name, edge) = ParseKey(mark.Key);
            if (kind != MarkKind.Scene || name is null) continue;
            if (edge == "start")
            {
                if (!starts.ContainsKey(name)) order.Add(name);
                starts[name] = mark.T;
            }
            else if (edge == "end")
            {
                ends[name] = mark.T;
            }
        }
        return order.Where(ends.ContainsKey)
            .Select(n => new KeyValuePair<string, (double Start, double End)>(n, (starts[n], ends[n])))
            .ToList();
    }

    /// <summary>Every fully bounded wait window as (start, end).</summary>
    public static List<(double Start, double End)> WaitWindows(IEnumerable<Mark> marks)
    {
        var windows = new List<(double, double)>();
        double? pending = null;
        foreach (var mark in marks)
        {
            var (kind, _, edge) = ParseKey(mark.Key);
            if (kind != MarkKind.Wait) continue;
            if (edge == "start") pending = mark.T;
            else if (edge == "end" && pending is not null)
            {
                windows.Add((pending.Value, mark.T));
                pending = null;
            }
        }
        return windows;
    }

    public static JsonArray ToJson(IEnumerable<Mark> marks)
    {
        var arr = new JsonArray();
        foreach (var m in marks)
            arr.Add(new JsonObject { ["t"] = m.T, ["key"] = m.Key, ["label"] = m.Label });
        return arr;
    }
}

/// <summary>
/// Incremental writer for a run's mark file. With a <c>path</c> the whole list is
/// rewritten after every <see cref="Add"/>, so a crashed take still leaves every mark
/// reached on disk.
/// </summary>
public sealed class Marks
{
    private readonly Func<double> _clock;
    private readonly List<Mark> _rows = new();

    /// <summary>Unix seconds (<c>time.time()</c>) by default.</summary>
    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public double T0 { get; set; }
    public string? Path { get; }

    public Marks(double? t0 = null, string? path = null, Func<double>? clock = null)
    {
        _clock = clock ?? Now;
        T0 = t0 ?? _clock();
        Path = path;
        if (Path is not null)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            Flush();
        }
    }

    public IReadOnlyList<Mark> Rows => _rows.ToList();

    public Mark Add(string key, string label = "")
    {
        // Python's round(), not Math.Round: the marks file must carry the same hundredths
        // whichever version wrote it (Math.Round(2.675, 2) is 2.68; Python says 2.67).
        var mark = new Mark(Fmt.PyRound(_clock() - T0, 2), key, label);
        _rows.Add(mark);
        if (Path is not null) Flush();
        return mark;
    }

    public void Write(string path)
    {
        File.WriteAllText(path, Json.Dump(MarkKeys.ToJson(_rows)), new System.Text.UTF8Encoding(false));
    }

    private void Flush() => Write(Path!);

    /// <summary>Marks the start of a scene now and its end when the returned scope is disposed.</summary>
    public IDisposable Scene(string name, string label = "")
    {
        Add(MarkKeys.SceneKey(name, "start"), label);
        return new Scope(() => Add(MarkKeys.SceneKey(name, "end"), label));
    }

    /// <summary>Marks the start of a wait now and its end when the returned scope is disposed.</summary>
    public IDisposable Wait(string label = "")
    {
        Add(MarkKeys.WaitKey("start"), label);
        return new Scope(() => Add(MarkKeys.WaitKey("end"), label));
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            onDispose();
        }
    }
}
