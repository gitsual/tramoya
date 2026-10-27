using System.Text.Json.Nodes;

namespace Tramoya.Core;

/// <summary>
/// The offset sidecar written next to a screen recording: how far the director's
/// own clock trails or leads the recorder's (the part of <c>tramoya/record.py</c>
/// the CLI still needs on Windows; wf-recorder and the PulseAudio sink stay on Linux).
/// </summary>
public static class Sidecar
{
    /// <summary>The sidecar recorded alongside a take.</summary>
    public static Dictionary<string, double> OffsetSidecar(double recStart, double directorStart) => new()
    {
        ["rec_start"] = recStart,
        ["director_start"] = directorStart,
        ["offset"] = directorStart - recStart,
    };

    public static void WriteSidecar(string path, IReadOnlyDictionary<string, double> sidecar)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in sidecar) obj[key] = value;
        // System.Text.Json indents with Environment.NewLine; the Python side writes "\n".
        var text = Json.Dump(obj, indent: true, trailingNewline: true).Replace("\r\n", "\n");
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
    }

    /// <summary>Read a sidecar's offset back.</summary>
    public static double ReadSidecar(string path)
    {
        var data = Json.LoadNode(path)?.AsObject() ?? throw new InvalidOperationException($"{path} is not a sidecar");
        var offset = data["offset"] ?? throw new KeyNotFoundException("offset");
        return offset.GetValue<double>();
    }
}
