using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tramoya.Core;

/// <summary>
/// JSON the way the Python package writes it: UTF-8 without escaping non-ASCII
/// (<c>ensure_ascii=False</c>), two-space indent, and a trailing newline where
/// the Python code adds one. Both implementations must read each other's files.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null,
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serialize with two-space indentation (Python's <c>indent=2</c>).</summary>
    public static string Dump(object? value, bool indent = true, bool trailingNewline = false)
    {
        var text = JsonSerializer.Serialize(value, indent ? Pretty : Compact);
        return trailingNewline ? text + "\n" : text;
    }

    public static string Dump(JsonNode? node, bool indent = true, bool trailingNewline = false)
    {
        var text = node is null ? "null" : node.ToJsonString(indent ? Pretty : Compact);
        return trailingNewline ? text + "\n" : text;
    }

    public static JsonNode? Parse(string text) => JsonNode.Parse(text);

    public static T? Load<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path, System.Text.Encoding.UTF8), Pretty);

    public static JsonNode? LoadNode(string path) => Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));

    public static void Write(string path, object? value, bool trailingNewline = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Dump(value, true, trailingNewline), new System.Text.UTF8Encoding(false));
    }

    public static void Write(string path, JsonNode? node, bool trailingNewline = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Dump(node, true, trailingNewline), new System.Text.UTF8Encoding(false));
    }
}
