using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tramoya.Core;

/// <summary>DevTools Recorder and Playwright codegen exports become one scene per navigation.</summary>
public static class Recording
{
    private static (string Path, string Query) UrlParts(string url)
    {
        string rest = Regex.Replace(url, @"^[a-zA-Z][a-zA-Z0-9+.-]*://[^/?#]*", "");
        rest = rest.Split('#')[0];
        int q = rest.IndexOf('?');
        string path = q < 0 ? rest : rest[..q];
        int parameters = path.IndexOf(';', path.LastIndexOf('/') + 1);
        if (parameters >= 0) path = path[..parameters];
        return (path, q < 0 ? "" : rest[(q + 1)..]);
    }
    private static string SceneName(string url, int index)
    {
        string path = UrlParts(url).Path.Split('/')[^1];
        string stem = Regex.Replace(path, @"\.[a-z0-9]+$", "");
        stem = Regex.Replace(stem.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return stem.Length > 0 ? stem : index == 0 ? "index" : $"page-{index + 1}";
    }
    private static string Relative(string url)
    {
        var (path, query) = UrlParts(url);
        return (path.Length > 0 ? path : "/") + (query.Length > 0 ? "?" + query : "");
    }
    private static void Finish(List<Scene> scenes, Scene? current)
    {
        if (current is null) return;
        if (current.Steps.Count == 0 || current.Steps[^1] != "say") current.Steps.Add("say");
        scenes.Add(current);
    }
    private static (string Kind, string Value)? PickSelector(JsonNode? selectors)
    {
        string? css = null, text = null, aria = null, xpath = null;
        foreach (var group in selectors?.AsArray() ?? new JsonArray())
            foreach (var node in group!.AsArray())
            {
                string s = node!.GetValue<string>();
                if (s.StartsWith("aria/")) { if (string.IsNullOrEmpty(aria)) aria = s[5..].Split('[')[0]; }
                else if (s.StartsWith("text/")) { if (string.IsNullOrEmpty(text)) text = s[5..]; }
                else if (s.StartsWith("xpath/")) { if (string.IsNullOrEmpty(xpath)) xpath = s[6..]; }
                else if (!s.StartsWith("pierce/") && string.IsNullOrEmpty(css)) css = s;
            }
        if (!string.IsNullOrEmpty(css)) return ("css", css);
        if (!string.IsNullOrEmpty(text)) return ("text", text);
        if (!string.IsNullOrEmpty(aria)) return ("text", aria);
        return string.IsNullOrEmpty(xpath) ? null : ("xpath", xpath);
    }
    private static string Target(string kind, string value) => kind == "css" ? value : kind + "=" + value;
    public static List<Scene> ScenesFromDevtools(JsonNode doc)
    {
        var scenes = new List<Scene>(); Scene? current = null;
        foreach (var step in doc["steps"]?.AsArray() ?? new JsonArray())
        {
            string? kind = step?["type"]?.GetValue<string>();
            string Get(string key) => step?[key]?.ToString() ?? "";
            if (kind == "navigate")
            {
                string url = Get("url"); Finish(scenes, current); string name = SceneName(url, scenes.Count);
                current = new(name, name, steps: scenes.Count == 0 ? [] : ["goto " + Relative(url)]); continue;
            }
            if (kind is not ("click" or "change" or "keyDown" or "scroll")) continue;
            current ??= new("recorded", "recorded");
            if (kind is "click" or "change")
            {
                if (PickSelector(step?["selectors"]) is not { } picked) continue;
                var (k, v) = picked;
                if (kind == "click") current.Steps.Add(k == "text" ? "click_text " + v : "click " + Target(k, v));
                else
                {
                    string verb = k == "css" && (v.StartsWith("#tag") || v.Contains("select", StringComparison.OrdinalIgnoreCase)) ? "select" : "type";
                    current.Steps.Add($"{verb} {Target(k, v)} | {Get("value")}");
                }
            }
            else if (kind == "keyDown") current.Steps.Add("press " + Get("key"));
            else
            {
                int dy = Get("y").Length == 0 ? 0 : (int)double.Parse(Get("y"), CultureInfo.InvariantCulture);
                if (dy != 0) current.Steps.Add("scroll " + dy.ToString(CultureInfo.InvariantCulture));
            }
        }
        Finish(scenes, current); return scenes;
    }
    private const string Quoted = "\"((?:[^\"\\\\]|\\\\.)*)\"";
    private static readonly Regex Locator = new("page\\.locator\\(" + Quoted + "\\)");
    private static readonly Regex Role = new("page\\.get_by_role\\(\"(\\w+)\",\\s*name=" + Quoted);
    private static readonly Regex Text = new("page\\.get_by_text\\(" + Quoted);
    private static readonly Regex Label = new("page\\.get_by_label\\(" + Quoted);
    private static readonly Regex Placeholder = new("page\\.get_by_placeholder\\(" + Quoted);
    private static readonly Regex Action = new(@"\.(click|fill|select_option|press|check|type)\((.*)\)\s*$");
    private static readonly Regex Goto = new("^page\\.goto\\(" + Quoted + "\\)");
    private static string Unquote(string text)
    {
        if (text.Length < 2 || text[0] is not ('\'' or '"') || text[^1] != text[0]) return text.Trim('"');
        var result = new StringBuilder();
        for (int i = 1; i < text.Length - 1; i++)
        {
            char c = text[i];
            if (c != '\\') { result.Append(c); continue; }
            if (++i >= text.Length - 1) return text.Trim('"');
            c = text[i];
            switch (c)
            {
                case '\\': case '\'': case '"': result.Append(c); break;
                case 'n': result.Append('\n'); break; case 't': result.Append('\t'); break;
                case 'r': result.Append('\r'); break; case 'b': result.Append('\b'); break;
                case 'f': result.Append('\f'); break; case 'v': result.Append('\v'); break;
                case 'a': result.Append('\a'); break; case '\n': break;
                case 'u': case 'U': case 'x':
                    int count = c == 'u' ? 4 : c == 'U' ? 8 : 2;
                    if (i + count >= text.Length - 1 || !int.TryParse(text.AsSpan(i + 1, count), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code)) return text.Trim('"');
                    if (code > 0x10ffff) return text.Trim('"');
                    result.Append(code <= 0xffff ? ((char)code).ToString() : char.ConvertFromUtf32(code)); i += count; break;
                default:
                    if (c is >= '0' and <= '7')
                    {
                        int value = c - '0', digits = 1;
                        while (digits < 3 && i + 1 < text.Length - 1 && text[i + 1] is >= '0' and <= '7') { value = value * 8 + text[++i] - '0'; digits++; }
                        result.Append((char)value);
                    }
                    else result.Append('\\').Append(c);
                    break;
            }
        }
        return result.ToString();
    }
    private static string Decode(string s) => Unquote("\"" + s + "\"");
    private static string? LocatorOf(string line)
    {
        Match m;
        if ((m = Locator.Match(line)).Success) return Decode(m.Groups[1].Value);
        if ((m = Role.Match(line)).Success) return $"role={m.Groups[1].Value}[name=\"{Decode(m.Groups[2].Value)}\"]";
        if ((m = Placeholder.Match(line)).Success) return $"[placeholder=\"{Decode(m.Groups[1].Value)}\"]";
        if ((m = Label.Match(line)).Success) return $"role=textbox[name=\"{Decode(m.Groups[1].Value)}\"]";
        return null;
    }
    public static List<Scene> ScenesFromCodegen(string source)
    {
        var scenes = new List<Scene>(); Scene? current = null;
        foreach (var raw in Regex.Split(source, "\\r\\n|[\\n\\r\\v\\f\\u0085\\u2028\\u2029]"))
        {
            string line = raw.Trim(); if (!line.StartsWith("page.")) continue;
            var m = Goto.Match(line);
            if (m.Success)
            {
                string url = Decode(m.Groups[1].Value); Finish(scenes, current); string name = SceneName(url, scenes.Count);
                current = new(name, name, steps: scenes.Count == 0 ? [] : ["goto " + Relative(url)]); continue;
            }
            current ??= new("recorded", "recorded");
            var action = Action.Match(line); if (!action.Success) continue;
            string verb = action.Groups[1].Value, arg = action.Groups[2].Value;
            if (verb == "press") { current.Steps.Add("press " + Unquote(arg)); continue; }
            m = Text.Match(line); string? locator;
            if (m.Success)
            {
                if (verb == "click") { current.Steps.Add("click_text " + Decode(m.Groups[1].Value)); continue; }
                locator = "text=" + Decode(m.Groups[1].Value);
            }
            else locator = LocatorOf(line);
            if (locator is null) continue;
            if (verb is "click" or "check") current.Steps.Add("click " + locator);
            else if (verb is "fill" or "type") current.Steps.Add($"type {locator} | {Unquote(arg)}");
            else if (verb == "select_option") current.Steps.Add($"select {locator} | {Unquote(arg)}");
        }
        Finish(scenes, current); return scenes;
    }
    public static List<Scene> ScenesFromFile(string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        if (text.TrimStart().StartsWith('{'))
        {
            var doc = Json.Parse(text);
            if (doc is JsonObject obj && obj.ContainsKey("steps")) return ScenesFromDevtools(doc);
        }
        if (text.Contains("page.")) return ScenesFromCodegen(text);
        throw new ArgumentException($"{path} is neither a DevTools Recorder export nor a playwright codegen script");
    }
}
