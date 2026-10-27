using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tramoya.Easy;

public sealed class Escena
{
    public string Name { get; set; } = "";
    public string Texto { get; set; } = "";
    public List<JsonObject> Items { get; set; } = [];
}

public static class Pasos
{
    public static string Str(JsonObject p, string key) => p[key]?.ToString() ?? "";
    public static bool Bool(JsonObject p, string key) => p[key] switch
    {
        null => false,
        JsonArray a => a.Count != 0,
        JsonObject o => o.Count != 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length != 0,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        _ => true
    };

    // Se conservan las referencias: Python también numera y modifica los pasos recibidos.
    public static List<JsonObject> Limpiar(List<JsonObject> pasos)
    {
        List<JsonObject> limpios = [];
        foreach (var p in pasos)
        {
            var prev = limpios.LastOrDefault();
            var mismo = prev is not null && JsonNode.DeepEquals(prev["selector"], p["selector"]);
            if (mismo && Str(p, "tipo") == "escribir" && Str(prev!, "tipo") is "escribir" or "clic")
                limpios[^1] = p;
            else if (mismo && Str(p, "tipo") == "clic" && Str(prev!, "tipo") == "escribir")
                continue;
            else limpios.Add(p);
        }
        var ultimo = limpios.FindLastIndex(p => Str(p, "tipo") != "tecla");
        for (var i = 0; i < limpios.Count; i++)
        {
            var p = limpios[i];
            p["id"] = i + 1;
            if (!p.ContainsKey("texto")) p["texto"] = "";
            if (Str(p, "tipo") == "clic") p["seguro"] = Bool(p, "guarda") && i == ultimo;
        }
        return limpios;
    }

    public static List<string> LineasDePaso(JsonObject p)
    {
        var s = Str(p, "selector");
        var v = Str(p, "valor");
        return Str(p, "tipo") switch
        {
            "clic" => Bool(p, "seguro") ? [$"point {s}", "beat 0.8"] : [$"click {s}", "beat 0.7"],
            "escribir" => [$"clear {s}", $"type {s} | {v}", "beat 0.3"],
            "tecla" => [$"press {v}", "beat 0.5"],
            "elegir" => [$"select {s} | {v}", "beat 0.6"],
            "ir" => [$"goto {v}", "beat 1"],
            _ => []
        };
    }

    public static double SegundosLectura(string texto) => Math.Round(
        Math.Max(2.5, Regex.Matches(texto, @"\S+").Count / 2.6 + 1.2), 1, MidpointRounding.ToEven);

    public static List<Escena> Escenas(JsonObject proyecto)
    {
        List<Escena> lista = [];
        var pasos = proyecto["pasos"]!.AsArray().Cast<JsonObject>().Where(p => !Bool(p, "quitado")).ToList();
        if (pasos.Count == 0 || Str(pasos[0], "texto").Trim().Length == 0)
            lista.Add(new() { Name = "inicio", Texto = Str(proyecto, "nombre") });
        foreach (var p in pasos)
        {
            var texto = Str(p, "texto").Trim();
            if (texto.Length != 0 || lista.Count == 0)
                lista.Add(new() { Name = $"paso-{Str(p, "id")}", Texto = texto });
            lista[^1].Items.Add(p);
        }
        return lista;
    }

    public static string Srt(IEnumerable<(string Texto, double Dur)> tramos)
    {
        static string Marca(double t)
        {
            var ms = (long)Math.Round(t * 1000, MidpointRounding.ToEven);
            // La división entera de Python redondea hacia menos infinito.
            long Div(long n, long d) => (long)Math.Floor((double)n / d);
            long Mod(long n, long d) => n - Div(n, d) * d;
            return string.Create(CultureInfo.InvariantCulture, $"{Div(ms, 3600000):D2}:{Mod(Div(ms, 60000), 60):D2}:{Mod(Div(ms, 1000), 60):D2},{Mod(ms, 1000):D3}");
        }
        List<string> bloques = [];
        double t = 0;
        foreach (var (texto, dur) in tramos)
        {
            if (texto.Trim().Length != 0)
                bloques.Add($"{(bloques.Count + 1).ToString(CultureInfo.InvariantCulture)}\n{Marca(t)} --> {Marca(t + dur)}\n{texto.Trim()}\n");
            t += dur;
        }
        return string.Join("\n", bloques);
    }
}
