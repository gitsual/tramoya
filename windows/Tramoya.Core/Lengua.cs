namespace Tramoya.Core;

/// <summary>
/// The language of everything a person reads: the window, the steps it writes («Click the
/// “Save” button»), progress and errors, and the sentences the AI writes. English by default,
/// Spanish selectable (the window's Settings, <c>easy --lang</c>, or <c>TRAMOYA_LANG</c>).
/// </summary>
/// <remarks>
/// The recorder and the observer still name elements with the Spanish phrases they always
/// used («el botón», «la casilla»): those are the internal role names, and this class turns
/// them into the words of the chosen language at the edge.
/// </remarks>
public static class Lengua
{
    public static readonly IReadOnlyList<string> Idiomas = ["en", "es"];

    private static string _actual = Normalizar(Environment.GetEnvironmentVariable("TRAMOYA_LANG"));

    /// <summary>The current language; child processes inherit it through <c>TRAMOYA_LANG</c>.</summary>
    public static string Actual
    {
        get => _actual;
        set
        {
            _actual = Normalizar(value);
            Environment.SetEnvironmentVariable("TRAMOYA_LANG", _actual);
        }
    }

    public static string Normalizar(string? lang)
    {
        var l = (lang ?? "").Trim().ToLowerInvariant();
        if (l.Length > 2) l = l[..2];
        return Idiomas.Contains(l) ? l : "en";
    }

    /// <summary>The Spanish or the English text, by language.</summary>
    public static string T(string es, string en, string? lang = null) => Normalizar(lang ?? _actual) == "es" ? es : en;

    private static readonly Dictionary<string, string> RolesEn = new(StringComparer.Ordinal)
    {
        ["el botón"] = "button", ["el enlace"] = "link", ["la casilla"] = "checkbox", ["el campo"] = "field",
        ["la celda"] = "cell", ["la columna"] = "column", ["la imagen"] = "image", ["la pestaña"] = "tab",
        ["la opción del menú"] = "menu item", ["la opción"] = "option", ["el elemento"] = "item",
        ["el interruptor"] = "switch", ["el desplegable"] = "drop-down", ["la fila"] = "row", ["el título"] = "heading",
        ["el buscador"] = "search box", ["la lista"] = "list", ["el texto"] = "text",
    };

    private static readonly Dictionary<string, string> TeclasEs = new(StringComparer.Ordinal)
    {
        ["Enter"] = "Intro", ["Tab"] = "Tabulador", ["Escape"] = "Esc", ["Backspace"] = "Borrar",
        ["ArrowDown"] = "flecha abajo", ["ArrowUp"] = "flecha arriba",
    };

    private static readonly Dictionary<string, string> TeclasEn = new(StringComparer.Ordinal)
    {
        ["Enter"] = "Enter", ["Tab"] = "Tab", ["Escape"] = "Esc", ["Backspace"] = "Backspace",
        ["ArrowDown"] = "down arrow", ["ArrowUp"] = "up arrow",
    };

    /// <summary>An internal role phrase («el botón») in the language, without article in English.</summary>
    public static string Rol(string rol, string? lang = null) =>
        T(rol, RolesEn.GetValueOrDefault(rol, rol.Length == 0 ? "" : "item"), lang);

    /// <summary>The thing a step acts on: «el botón «Guardar»» / «the “Save” button».</summary>
    public static string Cosa(string rol, string nombre, string? lang = null)
    {
        if (T("es", "en", lang) == "es")
            return nombre.Length != 0 ? (rol.Length != 0 ? $"{rol} «{nombre}»" : $"«{nombre}»") : (rol.Length != 0 ? rol : "este elemento");
        var r = Rol(rol, "en");
        return nombre.Length != 0 ? (r.Length != 0 ? $"the “{nombre}” {r}" : $"“{nombre}”") : (r.Length != 0 ? $"the {r}" : "this item");
    }

    /// <summary>«Pulsa …», «Marca …», «Desmarca …» (marca: "on", "off" or "").</summary>
    public static string Clic(string cosa, string marca = "", string? lang = null) => marca switch
    {
        "on" => T($"Marca {cosa}", $"Check {cosa}", lang),
        "off" => T($"Desmarca {cosa}", $"Uncheck {cosa}", lang),
        _ => T($"Pulsa {cosa}", $"Click {cosa}", lang),
    };

    public static string Cookies(string desc, string? lang = null) =>
        desc + T(" (aviso de cookies: si no sale, se salta)", " (cookie notice: skipped if it does not appear)", lang);

    public static string Escribe(string valor, string campo, string? lang = null) => campo.Length != 0
        ? T($"Escribe «{valor}» en el campo «{campo}»", $"Type “{valor}” in the “{campo}” field", lang)
        : T($"Escribe «{valor}»", $"Type “{valor}”", lang);

    public static string Elige(string valor, string donde, string? lang = null) => donde.Length != 0
        ? T($"Elige «{valor}» en «{donde}»", $"Choose “{valor}” in “{donde}”", lang)
        : T($"Elige «{valor}»", $"Choose “{valor}”", lang);

    public static string Tecla(string tecla, string? lang = null) => T(
        $"Pulsa la tecla {TeclasEs.GetValueOrDefault(tecla, tecla)}",
        $"Press {TeclasEn.GetValueOrDefault(tecla, tecla)}", lang);

    public static string Ve(string url, string? lang = null) => T($"Ve a «{url}»", $"Go to “{url}”", lang);

    public static string Senala(string cosa, string? lang = null) => T($"Señala {cosa}", $"Point at {cosa}", lang);

    /// <summary>The language the AI writes its sentences in, named for the prompt.</summary>
    public static string NombreParaModelo(string? lang = null) => T("español", "English", lang);
}
