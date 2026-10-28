using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tramoya.Core;

namespace Tramoya.Easy;

public static class CodegenSteps
{
    private sealed record Parte(string Metodo, List<object?> Args, Dictionary<string, object?> Kw);
    private static readonly object Unknown = new();
    private sealed record Token(string Tipo, string Valor);
    private static string Q(string t) => Json.Dump(t, indent: false);
    private static string? Texto(object? o) => o as string;

    public static (List<JsonObject> Pasos, List<string> Avisos) PasosDeGrabacion(string fuente)
    {
        List<JsonObject> pasos = [];
        HashSet<string> avisos = [];
        foreach (var tokens in Expresiones(fuente))
        {
            if (!Cadena(tokens, out var raiz, out var partes)) continue;
            if (raiz != "page")
            {
                if (raiz.StartsWith("page", StringComparison.Ordinal))
                    avisos.Add("Se abrió otra ventana durante la grabación; lo que hiciste en ella no se puede repetir.");
                continue;
            }
            var ultima = partes[^1];
            var accion = ultima.Metodo;
            var a0 = ultima.Args.Count > 0 ? Texto(ultima.Args[0]) : null;
            if (partes[0].Metodo == "keyboard")
            {
                if (accion == "press" && !string.IsNullOrEmpty(a0))
                    pasos.Add(new() { ["tipo"] = "tecla", ["valor"] = a0, ["desc"] = $"Pulsa la tecla {Vocabulario.Teclas.GetValueOrDefault(a0, a0)}" });
                continue;
            }
            if (accion == "goto" && partes.Count == 1)
            {
                if (pasos.Count > 0) pasos.Add(new() { ["tipo"] = "ir", ["valor"] = a0, ["desc"] = "Abre otra página" });
                continue;
            }
            if (accion == "press" && partes.Count == 1) continue;
            string sel, nombre;
            try { (sel, nombre) = partes.Count > 1 ? Selector(partes.Take(partes.Count - 1)) : ("", ""); }
            catch (ArgumentException) { Avisar(); continue; }
            if (sel.Length == 0) continue;
            if (accion is "click" or "dblclick" or "check" or "uncheck")
            {
                var verbo = accion == "check" ? "Marca" : accion == "uncheck" ? "Desmarca" : "Pulsa";
                if (accion is "check" or "uncheck" && !nombre.StartsWith("la casilla", StringComparison.Ordinal))
                    nombre = "la casilla" + (nombre == "este elemento" ? "" : $" de {nombre}");
                var limpio = Regex.Replace(nombre, "^.*«(.*)»$", "$1");
                pasos.Add(new() { ["tipo"] = "clic", ["selector"] = sel, ["desc"] = $"{verbo} {nombre}", ["guarda"] = Vocabulario.Guardan.IsMatch(limpio) });
            }
            else if (accion is "fill" or "type" or "press_sequentially" && a0 is not null)
            {
                if (Vocabulario.Clave.IsMatch(sel) || Vocabulario.Clave.IsMatch(nombre))
                { avisos.Add("He quitado un paso donde se escribía una contraseña."); continue; }
                pasos.Add(new() { ["tipo"] = "escribir", ["selector"] = sel, ["valor"] = a0,
                    ["desc"] = $"Escribe «{a0}»" + (nombre == "este elemento" ? "" : $" en {nombre}") });
            }
            else if (accion == "press" && !string.IsNullOrEmpty(a0))
                pasos.Add(new() { ["tipo"] = "tecla", ["selector"] = sel, ["valor"] = a0, ["desc"] = $"Pulsa la tecla {Vocabulario.Teclas.GetValueOrDefault(a0, a0)}" });
            else if (accion == "select_option" && !string.IsNullOrEmpty(a0))
                pasos.Add(new() { ["tipo"] = "elegir", ["selector"] = sel, ["valor"] = a0, ["desc"] = $"Elige «{a0}» en {nombre}" });
            else if (accion is not ("close" or "wait_for_timeout" or "set_default_timeout")) Avisar();
            void Avisar() => avisos.Add($"Un paso no se ha podido entender y se ha quitado ({accion}).");
        }
        return (Pasos.Limpiar(pasos), avisos.Order(StringComparer.Ordinal).ToList());
    }

    private static (string, string) Selector(IEnumerable<Parte> partes)
    {
        List<string> sel = [];
        var nombre = "";
        foreach (var p in partes)
        {
            var a = p.Args.Count > 0 ? Texto(p.Args[0]) : null;
            var ht = Texto(p.Kw.GetValueOrDefault("has_text"));
            var flag = p.Kw.GetValueOrDefault("exact") is true ? "s" : "i";
            switch (p.Metodo)
            {
                case "locator" when !string.IsNullOrEmpty(a):
                    sel.Add(a);
                    if (!string.IsNullOrEmpty(ht)) { sel.Add($"internal:has-text={Q(ht)}i"); nombre = ht; }
                    break;
                case "get_by_role" when !string.IsNullOrEmpty(a):
                    var n = Texto(p.Kw.GetValueOrDefault("name"));
                    sel.Add($"internal:role={a}" + (string.IsNullOrEmpty(n) ? "" : $"[name={Q(n)}{flag}]"));
                    var rol = Vocabulario.Roles.GetValueOrDefault(a, "el elemento");
                    nombre = !string.IsNullOrEmpty(n) ? $"{rol} «{n}»" : nombre.Length > 0 ? $"{rol} de {nombre}" : rol;
                    break;
                case "get_by_text" when !string.IsNullOrEmpty(a): sel.Add($"internal:text={Q(a)}{flag}"); nombre = $"«{a}»"; break;
                case "get_by_label" when !string.IsNullOrEmpty(a): sel.Add($"internal:label={Q(a)}{flag}"); nombre = $"el campo «{a}»"; break;
                case "get_by_placeholder" or "get_by_title" or "get_by_alt_text" when !string.IsNullOrEmpty(a):
                    var attr = p.Metodo == "get_by_placeholder" ? "placeholder" : p.Metodo == "get_by_title" ? "title" : "alt";
                    sel.Add($"internal:attr=[{attr}={Q(a)}{flag}]"); nombre = $"«{a}»"; break;
                case "get_by_test_id" when !string.IsNullOrEmpty(a): sel.Add($"internal:testid=[data-testid={Q(a)}s]"); break;
                case "filter": if (!string.IsNullOrEmpty(ht)) sel.Add($"internal:has-text={Q(ht)}i"); break;
                case "nth" when p.Args.Count > 0 && !ReferenceEquals(p.Args[0], Unknown):
                    var v = p.Args[0];
                    sel.Add("nth=" + (v is bool b ? b ? "True" : "False" : v is null ? "None" : Convert.ToString(v, CultureInfo.InvariantCulture))); break;
                case "first": sel.Add("nth=0"); break;
                case "last": sel.Add("nth=-1"); break;
                case "frame_locator" when !string.IsNullOrEmpty(a): sel.Add(a); sel.Add("internal:control=enter-frame"); break;
                case "content_frame": sel.Add("internal:control=enter-frame"); break;
                default: throw new ArgumentException(p.Metodo);
            }
        }
        return (string.Join(" >> ", sel), nombre.Length > 0 ? nombre : "este elemento");
    }

    // Los límites se calculan antes de analizar: los saltos dentro de llamadas o cadenas
    // no terminan una expresión, y las asignaciones nunca se confunden con acciones.
    private static IEnumerable<List<Token>> Expresiones(string fuente)
    {
        List<Token> linea = [];
        var profundidad = 0;
        for (var i = 0; i < fuente.Length;)
        {
            var c = fuente[i];
            if (c == '#') { while (i < fuente.Length && fuente[i] != '\n') i++; continue; }
            if (c is '\n' or ';' && profundidad == 0)
            { if (linea.Count > 0) yield return linea; linea = []; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '\\' && i + 1 < fuente.Length && fuente[i + 1] is '\r' or '\n') { i += 2; continue; }
            var raw = c is 'r' or 'R' && i + 1 < fuente.Length && fuente[i + 1] is '\'' or '"';
            if (c is '\'' or '"' || raw)
            {
                if (raw) c = fuente[++i];
                var triple = i + 2 < fuente.Length && fuente[i + 1] == c && fuente[i + 2] == c;
                i += triple ? 3 : 1;
                var b = new StringBuilder();
                while (i < fuente.Length)
                {
                    if (fuente[i] == c && (!triple || i + 2 < fuente.Length && fuente[i + 1] == c && fuente[i + 2] == c))
                    { i += triple ? 3 : 1; break; }
                    var ch = fuente[i++];
                    if (ch != '\\' || i == fuente.Length) { b.Append(ch); continue; }
                    var e = fuente[i++];
                    if (raw) { b.Append('\\').Append(e); continue; }
                    if (e is '\n') continue;
                    if (e == '\r') { if (i < fuente.Length && fuente[i] == '\n') i++; continue; }
                    if (e is 'x' or 'u' or 'U')
                    {
                        var len = e == 'x' ? 2 : e == 'u' ? 4 : 8;
                        if (i + len <= fuente.Length && int.TryParse(fuente.AsSpan(i, len), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                        { b.Append(cp <= 0xffff ? ((char)cp).ToString() : char.ConvertFromUtf32(cp)); i += len; continue; }
                    }
                    if (e is >= '0' and <= '7')
                    {
                        var oct = e - '0'; var count = 1;
                        while (count < 3 && i < fuente.Length && fuente[i] is >= '0' and <= '7') { oct = oct * 8 + fuente[i++] - '0'; count++; }
                        b.Append((char)oct); continue;
                    }
                    b.Append(e switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', 'v' => '\v', 'a' => '\a', '\\' => '\\', '\'' => '\'', '"' => '"', _ => '\\' });
                    if (e is not ('n' or 'r' or 't' or 'b' or 'f' or 'v' or 'a' or '\\' or '\'' or '"')) b.Append(e);
                }
                linea.Add(new("str", b.ToString())); continue;
            }
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                var inicio = i++;
                while (i < fuente.Length && (char.IsLetterOrDigit(fuente[i]) || fuente[i] == '_')) i++;
                linea.Add(new("word", fuente[inicio..i])); continue;
            }
            if (c is '(' or '[' or '{') profundidad++;
            if (c is ')' or ']' or '}') profundidad--;
            linea.Add(new(c.ToString(), c.ToString())); i++;
        }
        if (linea.Count > 0) yield return linea;
    }

    private static bool Cadena(List<Token> ts, out string raiz, out List<Parte> partes)
    {
        raiz = ""; partes = [];
        if (ts.Count == 0 || ts[0].Tipo != "word") return false;
        raiz = ts[0].Valor;
        var i = 1; var llamada = false;
        while (i < ts.Count)
        {
            if (ts[i++].Tipo != "." || i >= ts.Count || ts[i].Tipo != "word") return false;
            var metodo = ts[i++].Valor;
            List<object?> args = []; Dictionary<string, object?> kw = [];
            llamada = i < ts.Count && ts[i].Tipo == "(";
            if (llamada)
            {
                i++;
                while (i < ts.Count && ts[i].Tipo != ")")
                {
                    string? key = null;
                    if (i + 1 < ts.Count && ts[i].Tipo == "word" && ts[i + 1].Tipo == "=") { key = ts[i].Valor; i += 2; }
                    if (i >= ts.Count) return false;
                    // An AST argument can be any expression. Only constants contribute
                    // values; nested calls and collections must not swallow the chain.
                    var inicio = i;
                    var cierres = new Stack<string>();
                    while (i < ts.Count)
                    {
                        var tipo = ts[i].Tipo;
                        if (cierres.Count == 0 && tipo is "," or ")") break;
                        if (tipo is "(" or "[" or "{")
                            cierres.Push(tipo == "(" ? ")" : tipo == "[" ? "]" : "}");
                        else if (tipo is ")" or "]" or "}")
                        {
                            if (cierres.Count == 0 || cierres.Pop() != tipo) return false;
                        }
                        i++;
                    }
                    if (i == inicio || cierres.Count > 0) return false;
                    object? valor = Unknown;
                    var t = ts[inicio];
                    if (t.Tipo == "str" && ts.GetRange(inicio, i - inicio).All(t => t.Tipo == "str"))
                        valor = string.Concat(ts.GetRange(inicio, i - inicio).Select(t => t.Valor));
                    else if (i == inicio + 1)
                    {
                        if (t.Tipo == "word" && t.Valor is "True" or "False" or "None")
                            valor = t.Valor == "None" ? null : t.Valor == "True";
                        else if (t.Tipo == "word" && long.TryParse(t.Valor, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                            valor = n;
                    }
                    if (key is null) args.Add(valor); else kw[key] = valor;
                    if (i < ts.Count && ts[i].Tipo == ",") i++;
                    else if (i >= ts.Count || ts[i].Tipo != ")") return false;
                }
                if (i >= ts.Count || ts[i++].Tipo != ")") return false;
            }
            partes.Add(new(metodo, args, kw));
        }
        return partes.Count > 0 && llamada;
    }
}
