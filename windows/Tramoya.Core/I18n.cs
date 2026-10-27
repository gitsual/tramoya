using System.Globalization;
using System.Text;

namespace Tramoya.Core;

/// <summary>
/// Small message-table translator, used for the narrator and console output
/// (port of <c>tramoya/i18n.py</c>).
/// </summary>
/// <remarks>
/// Looks up a key in a <c>{key: {lang: text}}</c> table and fills the <c>{name}</c>
/// placeholders the way Python's <c>str.format(**kw)</c> would. Falls back to
/// <see cref="DefaultLang"/> when the requested language has no entry for a key, and
/// to the bare key itself when the key is missing entirely.
/// </remarks>
public sealed class Messages
{
    private readonly Dictionary<string, Dictionary<string, string>> _table;

    public string DefaultLang { get; set; }

    public Messages(Dictionary<string, Dictionary<string, string>>? table = null, string defaultLang = "en")
    {
        _table = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (table is not null) Register(table);
        DefaultLang = defaultLang;
    }

    /// <summary>Merge another table into this one, overwriting existing keys.</summary>
    public void Register(Dictionary<string, Dictionary<string, string>> table)
    {
        foreach (var (key, entry) in table)
            _table[key] = new Dictionary<string, string>(entry, StringComparer.Ordinal);
    }

    public string T(string key, string? lang = null, params (string Name, object? Value)[] kw)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in kw) values[name] = value;
        return T(key, lang, values);
    }

    public string T(string key, string? lang, IDictionary<string, object?> kw)
    {
        if (!_table.TryGetValue(key, out var entry)) return key;
        var effectiveLang = lang ?? DefaultLang;
        if (!entry.TryGetValue(effectiveLang, out var template) && !entry.TryGetValue(DefaultLang, out template))
            template = key;
        return Format(template, kw);
    }

    /// <summary>
    /// The subset of Python's <c>str.format</c> the message tables use: <c>{name}</c>,
    /// <c>{name:.1f}</c> and the <c>{{</c>/<c>}}</c> escapes. A placeholder with no value
    /// is a <see cref="KeyNotFoundException"/>, like Python's <c>KeyError</c>, so a typo in
    /// a table shows up the first time the message is rendered.
    /// </summary>
    internal static string Format(string template, IDictionary<string, object?> kw)
    {
        var sb = new StringBuilder(template.Length);
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{') { sb.Append('{'); i++; continue; }
                var close = template.IndexOf('}', i + 1);
                if (close < 0) throw new FormatException("Single '{' encountered in format string");
                var field = template.Substring(i + 1, close - i - 1);
                var colon = field.IndexOf(':');
                var name = colon < 0 ? field : field[..colon];
                var spec = colon < 0 ? "" : field[(colon + 1)..];
                if (!kw.TryGetValue(name, out var value)) throw new KeyNotFoundException(name);
                sb.Append(FormatValue(value, spec));
                i = close;
            }
            else if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}') { sb.Append('}'); i++; continue; }
                throw new FormatException("Single '}' encountered in format string");
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Python's <c>format(value, spec)</c> for the value types a message can carry.</summary>
    private static string FormatValue(object? value, string spec)
    {
        if (spec.Length == 0)
        {
            return value switch
            {
                null => "None",
                bool b => b ? "True" : "False",
                double d => Fmt.Py(d),
                float f => Fmt.Py(f),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? "",
            };
        }
        // ".Nf": fixed decimals, the only spec the tables use besides none.
        if (spec.Length >= 3 && spec[0] == '.' && spec[^1] == 'f'
            && int.TryParse(spec[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var decimals))
            return Fmt.F(Convert.ToDouble(value, CultureInfo.InvariantCulture), decimals);
        if (spec == "d")
            return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        if (value is IFormattable formattable)
            return formattable.ToString(spec, CultureInfo.InvariantCulture);
        throw new FormatException($"unsupported format spec '{spec}' for {value?.GetType().Name ?? "None"}");
    }
}
