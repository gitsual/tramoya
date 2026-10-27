using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tramoya.Core;

namespace Tramoya.Easy;

/// <summary>
/// Los proyectos de «tramoya easy»: una carpeta por vídeo dentro de <c>Datos</c> con su
/// <c>proyecto.json</c>, y <c>ajustes.json</c> con las preferencias de la persona. Port de la
/// parte de ficheros de <c>tramoya/easy/app.py</c>.
/// </summary>
/// <remarks>
/// El Python trabaja con globales (<c>DATOS</c>, <c>MIS_VIDEOS</c>); aquí todo recibe las
/// <see cref="ServerOptions"/> para que los tests usen una carpeta temporal y para que la
/// ventana y el CLI decidan dónde viven los datos (kit portátil o <c>%LOCALAPPDATA%</c>).
/// El contenido de <c>proyecto.json</c> tiene que ser el mismo que escribe el Python: la
/// misma persona puede abrir el mismo proyecto con una versión u otra.
/// </remarks>
public static class Proyectos
{
    private static readonly Regex NoAlfanumerico = new("[^a-z0-9]+", RegexOptions.CultureInvariant);
    private static readonly Regex Separadores = new(@"[,\s]+", RegexOptions.CultureInvariant);
    private static readonly Regex Esquema = new("^https?://", RegexOptions.CultureInvariant);

    // Las letras del latín-1 y del latín extendido-A que NFKD descompone en una letra ASCII
    // más una marca (que después se tira). Las que NFKD no descompone (æ, ø, ł, ß, đ…) no
    // están y se van, como en el Python.
    private static readonly Dictionary<char, string> SinAcento = Tabla(
        ("A", "ÀÁÂÃÄÅĀĂĄ"), ("a", "àáâãäåāăą"), ("C", "ÇĆĈĊČ"), ("c", "çćĉċč"), ("D", "Ď"), ("d", "ď"),
        ("E", "ÈÉÊËĒĔĖĘĚ"), ("e", "èéêëēĕėęě"), ("G", "ĜĞĠĢ"), ("g", "ĝğġģ"), ("H", "Ĥ"), ("h", "ĥ"),
        ("I", "ÌÍÎÏĨĪĬĮİ"), ("i", "ìíîïĩīĭį"), ("IJ", "Ĳ"), ("ij", "ĳ"), ("J", "Ĵ"), ("j", "ĵ"),
        ("K", "Ķ"), ("k", "ķ"), ("L", "ĹĻĽĿ"), ("l", "ĺļľŀ"), ("N", "ÑŃŅŇ"), ("n", "ñńņňŉ"),
        ("O", "ÒÓÔÕÖŌŎŐ"), ("o", "òóôõöōŏő"), ("R", "ŔŖŘ"), ("r", "ŕŗř"), ("S", "ŚŜŞŠ"), ("s", "śŝşšſ"),
        ("T", "ŢŤ"), ("t", "ţť"), ("U", "ÙÚÛÜŨŪŬŮŰŲ"), ("u", "ùúûüũūŭůűų"), ("W", "Ŵ"), ("w", "ŵ"),
        ("Y", "ÝŶŸ"), ("y", "ýÿŷ"), ("Z", "ŹŻŽ"), ("z", "źżž"));

    private static Dictionary<char, string> Tabla(params (string Base, string Variantes)[] filas)
    {
        var d = new Dictionary<char, string>();
        foreach (var (b, variantes) in filas)
            foreach (var v in variantes)
                d[v] = b;
        return d;
    }

    /// <summary>Los ajustes de fábrica; solo estas claves se guardan (<c>AJUSTES_DEFECTO</c>).</summary>
    public static JsonObject AjustesDefecto() => new()
    {
        ["voz"] = "",
        ["velocidad"] = 0,
        ["ritmo"] = "normal",
        ["tamano"] = "800p",
        ["srt"] = true,
        ["dominios"] = new JsonArray(),
    };

    /// <summary>Segundos Unix con decimales, como <c>time.time()</c>.</summary>
    public static double Ahora() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

    public static string Carpeta(ServerOptions o, string pid) => Path.Combine(o.Datos, pid);
    public static string RutaProyecto(ServerOptions o, string pid) => Path.Combine(o.Datos, pid, "proyecto.json");
    public static bool Existe(ServerOptions o, string pid) => File.Exists(RutaProyecto(o, pid));

    /// <summary>
    /// Un identificador de carpeta a partir del nombre: sin acentos, en minúsculas, con guiones,
    /// 40 caracteres como mucho; «video» si no queda nada.
    /// </summary>
    public static string Slug(string texto)
    {
        // NFKD y quitar lo que no sea ASCII (las marcas diacríticas separadas se van con ello),
        // igual que `normalize("NFKD").encode("ascii", "ignore")`. La solución compila con
        // InvariantGlobalization y en ese modo Normalize devuelve el texto tal cual, así que las
        // letras latinas con acento se descomponen a mano: si no, «Mi vídeo» sería «mi-vdeo».
        var descompuesto = texto.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(descompuesto.Length);
        foreach (var c in descompuesto)
        {
            if (c < 128) sb.Append(c);
            else if (SinAcento.TryGetValue(c, out var basica)) sb.Append(basica);
        }
        var t = sb.ToString().ToLowerInvariant();
        t = NoAlfanumerico.Replace(t, "-").Trim('-');
        if (t.Length > 40) t = t[..40];
        return t.Length == 0 ? "video" : t;
    }

    public static JsonObject Leer(ServerOptions o, string pid) =>
        Json.Parse(File.ReadAllText(RutaProyecto(o, pid), Encoding.UTF8))!.AsObject();

    /// <summary>Escribe <c>proyecto.json</c> de forma atómica: primero un <c>.tmp</c> y luego el cambio de nombre.</summary>
    public static void Guardar(ServerOptions o, string pid, JsonObject datos)
    {
        var ruta = RutaProyecto(o, pid);
        var tmp = Path.ChangeExtension(ruta, ".tmp");
        File.WriteAllText(tmp, Json.Dump(datos, indent: true), new UTF8Encoding(false));
        File.Move(tmp, ruta, overwrite: true);
    }

    public static string RutaAjustes(ServerOptions o) => Path.Combine(o.Datos, "ajustes.json");

    /// <summary>Los ajustes guardados sobre los de fábrica; un fichero roto o ausente son los de fábrica.</summary>
    public static JsonObject LeerAjustes(ServerOptions o)
    {
        JsonObject guardados;
        try
        {
            guardados = Json.Parse(File.ReadAllText(RutaAjustes(o), Encoding.UTF8)) as JsonObject ?? new JsonObject();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            guardados = new JsonObject();
        }
        var a = AjustesDefecto();
        foreach (var (k, v) in guardados)
            if (a.ContainsKey(k))
                a[k] = v?.DeepClone();
        return a;
    }

    /// <summary>
    /// Mezcla lo que manda la interfaz con lo guardado, ignorando claves desconocidas. Los
    /// dominios pueden llegar como texto («a.com, b.net») y se guardan como lista.
    /// </summary>
    public static JsonObject GuardarAjustes(ServerOptions o, JsonObject nuevos)
    {
        var a = LeerAjustes(o);
        foreach (var (k, v) in nuevos)
            if (a.ContainsKey(k))
                a[k] = v?.DeepClone();
        if (a["dominios"] is JsonValue dv && dv.TryGetValue<string>(out var texto))
        {
            var lista = new JsonArray();
            foreach (var d in Separadores.Split(texto))
                if (d.Trim().Length != 0)
                    lista.Add(d.Trim());
            a["dominios"] = lista;
        }
        Directory.CreateDirectory(o.Datos);
        File.WriteAllText(RutaAjustes(o), Json.Dump(a, indent: true), new UTF8Encoding(false));
        return a;
    }

    /// <summary>Todos los <c>proyecto.json</c> legibles de <c>Datos</c>; los rotos se saltan.</summary>
    private static List<JsonObject> Todos(ServerOptions o)
    {
        List<JsonObject> proyectos = [];
        if (!Directory.Exists(o.Datos)) return proyectos;
        foreach (var carpeta in Directory.EnumerateDirectories(o.Datos))
        {
            var ruta = Path.Combine(carpeta, "proyecto.json");
            if (!File.Exists(ruta)) continue;
            try
            {
                if (Json.Parse(File.ReadAllText(ruta, Encoding.UTF8)) is JsonObject p)
                    proyectos.Add(p);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                continue;
            }
        }
        return proyectos;
    }

    private static double Numero(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    /// <summary>La portada: id, nombre, fase, creado, si tiene vídeo y qué tarea corre; el más nuevo primero.</summary>
    public static JsonArray Lista(ServerOptions o)
    {
        var salida = new List<(double Creado, JsonObject Fila)>();
        foreach (var p in Todos(o))
        {
            var id = Pasos.Str(p, "id");
            salida.Add((Numero(p["creado"]), new JsonObject
            {
                ["id"] = id,
                ["nombre"] = p["nombre"]?.DeepClone(),
                ["fase"] = p["fase"]?.DeepClone(),
                ["creado"] = p["creado"]?.DeepClone() ?? 0,
                ["video"] = Pasos.Bool(p, "video"),
                ["tarea"] = Tareas.Tarea(id)["estado"]?.DeepClone(),
            }));
        }
        var lista = new JsonArray();
        foreach (var (_, fila) in salida.OrderByDescending(x => x.Creado))
            lista.Add(fila);
        return lista;
    }

    /// <summary>Las webs de los vídeos anteriores, la más reciente primero.</summary>
    public static JsonArray Webs(ServerOptions o)
    {
        HashSet<string> vistas = [];
        var salida = new JsonArray();
        foreach (var p in Todos(o).OrderByDescending(x => Numero(x["creado"])))
        {
            var url = Pasos.Str(p, "url");
            if (!vistas.Add(url)) continue;
            var titulo = Pasos.Str(p, "titulo_web");
            salida.Add(new JsonObject
            {
                ["url"] = url,
                ["nombre"] = titulo.Length != 0 ? titulo : NombreWeb(url),
            });
            if (salida.Count == 6) break;
        }
        return salida;
    }

    /// <summary>El nombre corto de una web: su host sin «www.», o la dirección tal cual si no hay host.</summary>
    public static string NombreWeb(string url)
    {
        string host = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length != 0)
            host = u.Host;
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>El primer id libre para un nombre: el slug, y si ya existe, «-2», «-3»…</summary>
    private static string IdLibre(ServerOptions o, string nombre)
    {
        var @base = Slug(nombre);
        var pid = @base;
        for (var n = 2; Directory.Exists(Carpeta(o, pid)) || File.Exists(Carpeta(o, pid)); n++)
            pid = $"{@base}-{n}";
        return pid;
    }

    /// <summary>
    /// Copia un proyecto como «<i>nombre</i> (copia)» sin sus salidas (<c>out</c>, <c>*.txt</c>)
    /// ni su vídeo; vuelve a la fase de explicar si ya tenía pasos.
    /// </summary>
    public static string Duplicar(ServerOptions o, string pid)
    {
        var p = Leer(o, pid);
        var nombre = $"{Pasos.Str(p, "nombre")} (copia)";
        var nuevo = IdLibre(o, nombre);
        CopiarArbol(Carpeta(o, pid), Carpeta(o, nuevo));
        p["id"] = nuevo;
        p["nombre"] = nombre;
        p["creado"] = Ahora();
        p["video"] = null;
        p["srt"] = null;
        p["fase"] = Pasos.Bool(p, "pasos") ? "explicar" : "grabar";
        Guardar(o, nuevo, p);
        return nuevo;
    }

    // shutil.copytree(..., ignore=ignore_patterns("out", "*.txt")): los patrones se aplican a
    // cualquier nivel, a carpetas y a ficheros.
    private static void CopiarArbol(string origen, string destino)
    {
        Directory.CreateDirectory(destino);
        foreach (var ruta in Directory.EnumerateFileSystemEntries(origen))
        {
            var nombre = Path.GetFileName(ruta);
            if (nombre == "out" || nombre.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                continue;
            var aqui = Path.Combine(destino, nombre);
            if (Directory.Exists(ruta))
                CopiarArbol(ruta, aqui);
            else
                File.Copy(ruta, aqui, overwrite: true);
        }
    }

    /// <summary>
    /// Un proyecto nuevo (la lógica de <c>/api/crear</c>): nombre por defecto «Mi vídeo», la
    /// dirección con <c>https://</c> si no trae esquema. Sin dirección no hay proyecto:
    /// <see cref="ArgumentException"/> con el mensaje para la persona.
    /// </summary>
    public static string Crear(ServerOptions o, string? nombre, string? url)
    {
        nombre = (nombre ?? "").Trim();
        if (nombre.Length == 0) nombre = "Mi vídeo";
        url = (url ?? "").Trim();
        if (url.Length == 0)
            throw new ArgumentException("Falta la dirección de la web");
        if (!Esquema.IsMatch(url))
            url = "https://" + url;
        var pid = IdLibre(o, nombre);
        Directory.CreateDirectory(Carpeta(o, pid));
        Guardar(o, pid, new JsonObject
        {
            ["id"] = pid,
            ["nombre"] = nombre,
            ["url"] = url,
            ["pasos"] = new JsonArray(),
            ["fase"] = "grabar",
            ["creado"] = Ahora(),
        });
        return pid;
    }

    /// <summary>La marca de «hecho» que enseña la interfaz: <c>%d-%m-%Y %H:%M</c> en hora local.</summary>
    public static string Hecho() => DateTime.Now.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);
}
