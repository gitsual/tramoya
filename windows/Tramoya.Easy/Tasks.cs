using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tramoya.Core;

namespace Tramoya.Easy;

/// <summary>
/// Los trabajos pesados de «tramoya easy» (abrir la web y grabar, hacer el vídeo, oír una
/// voz, listar las voces) corren en procesos hijos <c>Tramoya.exe easy-engine …</c>, como en
/// el Python corren en <c>python -m tramoya.easy.engine</c>: si un navegador se cae, no se
/// lleva la ventana por delante. Port de <c>TAREAS</c>, <c>lanzar</c>, <c>abrir_web</c>,
/// <c>orden</c>, <c>convertir</c>, <c>hacer_video</c>, <c>cancelar</c>, <c>oir</c> y
/// <c>voces</c> de <c>tramoya/easy/app.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// El motor habla por su salida estándar con una línea JSON por evento; aquí se lee línea a
/// línea en un hilo de fondo y se va actualizando el estado que la interfaz consulta por
/// <c>/api/p/&lt;id&gt;</c>: <c>quieto</c>, <c>abriendo</c>, <c>preparado</c>, <c>grabando</c>,
/// <c>convirtiendo</c>, <c>haciendo</c> (con <c>msg</c> y <c>p</c>), <c>listo</c> y
/// <c>error</c> (con <c>msg</c> y a veces <c>paso</c>).
/// </para>
/// <para>
/// Un trabajo a la vez por vídeo. El Python se apoya en el GIL para tocar el diccionario
/// desde varios hilos; aquí todo lo que lee o cambia una tarea pasa por <see cref="Cerrojo"/>,
/// que es también el cerrojo que el servidor toma al atender una acción (<c>CERROJO</c>).
/// </para>
/// </remarks>
public static class Tareas
{
    /// <summary>El estado de un vídeo y, si hay trabajo en marcha, su proceso.</summary>
    private sealed class Entrada
    {
        public JsonObject Datos = new() { ["estado"] = "quieto" };
        public Process? Proc;
    }

    // id -> {"estado", "msg", "p", "proc", ...}; un trabajo a la vez por vídeo
    private static readonly Dictionary<string, Entrada> Mapa = new();
    private static readonly List<Process> Oyendo = [];
    private static List<string>? Voces_;

    /// <summary>El cerrojo de las acciones: una por vez, como <c>CERROJO</c> en el Python.</summary>
    public static readonly object Cerrojo = new();

    private static JsonObject Estado(string estado) => new() { ["estado"] = estado };

    /// <summary>El estado de un vídeo tal como lo ve la interfaz (sin el proceso).</summary>
    public static JsonObject Tarea(string pid)
    {
        lock (Cerrojo)
            return Mapa.TryGetValue(pid, out var e) ? (JsonObject)e.Datos.DeepClone() : Estado("quieto");
    }

    /// <summary>Sesiones de las webs, como en un navegador normal.</summary>
    public static string PerfilWeb(ServerOptions o) => Path.Combine(o.Datos, "_navegador");

    /// <summary>
    /// Un proceso del motor: sin ventana de consola, con la salida (y los errores) en una
    /// tubería UTF-8 y el entorno de la app más lo que añaden las opciones (ruta de Chromium,
    /// PATH con ffmpeg).
    /// </summary>
    private static ProcessStartInfo Preparar(ServerOptions o, IEnumerable<string> argv, string? cwd, bool tuberias)
    {
        var lista = o.Motor.Concat(argv).ToList();
        var psi = new ProcessStartInfo(lista[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd ?? "",
            RedirectStandardOutput = tuberias,
            RedirectStandardError = tuberias,
        };
        foreach (var a in lista.Skip(1)) psi.ArgumentList.Add(a);
        if (tuberias)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }
        foreach (var (k, v) in o.Entorno) psi.Environment[k] = v;
        return psi;
    }

    private static Process Lanzar(ServerOptions o, string pid, IEnumerable<string> argv, bool entrada = false)
    {
        var psi = Preparar(o, argv, Proyectos.Carpeta(o, pid), tuberias: true);
        psi.RedirectStandardInput = entrada;
        if (entrada) psi.StandardInputEncoding = new UTF8Encoding(false);
        return Process.Start(psi) ?? throw new InvalidOperationException($"No se ha podido lanzar {psi.FileName}");
    }

    /// <summary>
    /// Lee la salida y los errores del proceso línea a línea en dos hilos (el Python los mezcla
    /// en una sola tubería), llama a <paramref name="linea"/> con cada una bajo el cerrojo y,
    /// cuando el proceso termina, a <paramref name="fin"/>.
    /// </summary>
    private static void Escuchar(Process proc, Action<string> linea, Action fin)
    {
        var candado = new object();
        Thread Lector(StreamReader r) => new(() =>
        {
            try
            {
                while (r.ReadLine() is { } l)
                    lock (candado) linea(l);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // El proceso murió o lo matamos: se acabó lo que había que leer.
            }
        }) { IsBackground = true };
        var hilos = new[] { Lector(proc.StandardOutput), Lector(proc.StandardError) };
        foreach (var h in hilos) h.Start();
        new Thread(() =>
        {
            foreach (var h in hilos) h.Join();
            try { proc.WaitForExit(); } catch (Exception e) when (e is InvalidOperationException or SystemException) { }
            fin();
        }) { IsBackground = true }.Start();
    }

    private static bool EsMio(string pid, Process proc) =>
        Mapa.TryGetValue(pid, out var e) && ReferenceEquals(e.Proc, proc);

    private static JsonObject? Linea(string texto)
    {
        try
        {
            return Json.Parse(texto) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // --- grabar: una ventana con la web; se apunta desde que se pulsa «Empezar» ------

    /// <summary>
    /// Abre la web en la ventana del grabador. El motor avisa de <c>preparado</c>, de
    /// <c>grabando</c> y de cada paso apuntado; al cerrarse la ventana, si llegó a empezar,
    /// la grabación se convierte en pasos del proyecto.
    /// </summary>
    public static void AbrirWeb(ServerOptions o, string pid)
    {
        var p = Proyectos.Leer(o, pid);
        string[] argv = ["grabar", Proyectos.Carpeta(o, pid), Pasos.Str(p, "url"), PerfilWeb(o),
            Json.Dump(Proyectos.LeerAjustes(o), indent: false)];
        var proc = Lanzar(o, pid, argv, entrada: true);
        lock (Cerrojo)
            Mapa[pid] = new Entrada { Datos = Estado("abriendo"), Proc = proc };

        JsonObject fin = new();
        Escuchar(proc, linea =>
        {
            if (Linea(linea) is not { } d) return;
            lock (Cerrojo)
            {
                if (!EsMio(pid, proc)) return;
                var t = Mapa[pid].Datos;
                var estado = Pasos.Str(d, "estado");
                if (estado is "preparado" or "grabando")
                    t["estado"] = estado;
                if (d.ContainsKey("paso"))
                {
                    t["ultimo"] = d["paso"]?.DeepClone();
                    t["n"] = d["n"]?.DeepClone();
                }
                if (Pasos.Bool(d, "fin"))
                    fin = d;
            }
        }, () =>
        {
            lock (Cerrojo)
            {
                if (!EsMio(pid, proc)) return;
                if (fin["inicio"] is null)
                {
                    Mapa[pid] = new Entrada(); // cerró la ventana sin empezar
                    return;
                }
                Convertir(o, pid);
            }
        });
    }

    /// <summary>Le dice al grabador «empezar» o «terminar» por su entrada estándar.</summary>
    public static void Orden(string pid, string texto)
    {
        Process? proc;
        lock (Cerrojo)
        {
            if (!Mapa.TryGetValue(pid, out var t)) return;
            proc = t.Proc;
            if (proc is null || proc.HasExited) return;
            if (texto == "terminar")
                t.Datos["estado"] = "convirtiendo";
        }
        try
        {
            proc.StandardInput.Write(texto + "\n");
            proc.StandardInput.Flush();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // El grabador ya no escucha; la interfaz lo verá al refrescar.
        }
    }

    /// <summary>
    /// Pasa <c>grabacion.json</c> al proyecto: los pasos, los avisos, el instante de inicio y
    /// el título de la web. Al regrabar, se conservan los textos de los pasos iguales.
    /// </summary>
    public static void Convertir(ServerOptions o, string pid)
    {
        var ruta = Path.Combine(Proyectos.Carpeta(o, pid), "grabacion.json");
        JsonObject res;
        try
        {
            res = Json.Parse(File.ReadAllText(ruta, Encoding.UTF8)) as JsonObject ?? throw new JsonException();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Poner(pid, Estado("error"), "msg", "No he podido leer la grabación.");
            return;
        }
        var pasos = res["pasos"] as JsonArray;
        if (pasos is null || pasos.Count == 0)
        {
            Poner(pid, Estado("error"), "msg", "No se ha apuntado ningún paso. Pulsa «Empezar "
                + "a grabar» y luego haz los clics en la ventana de la web.");
            return;
        }
        var p = Proyectos.Leer(o, pid);
        var viejos = new Dictionary<string, JsonNode?>();
        foreach (var x in (p["pasos"] as JsonArray ?? []).OfType<JsonObject>())
            viejos[Pasos.Str(x, "desc")] = x["texto"]?.DeepClone() ?? "";
        foreach (var x in pasos.OfType<JsonObject>())
            x["texto"] = viejos.TryGetValue(Pasos.Str(x, "desc"), out var texto) ? texto?.DeepClone() ?? "" : "";
        p["pasos"] = pasos.DeepClone();
        p["avisos"] = res["avisos"]?.DeepClone();
        p["inicio"] = res["inicio"]?.DeepClone();
        p["fase"] = "explicar";
        p["video"] = null;
        var titulo = res["titulo"];
        p["titulo_web"] = Pasos.Bool(res, "titulo") ? titulo!.DeepClone() : p["titulo_web"]?.DeepClone();
        Proyectos.Guardar(o, pid, p);
        Poner(pid, Estado("quieto"));
    }

    private static void Poner(string pid, JsonObject datos, string? clave = null, string? valor = null)
    {
        if (clave is not null) datos[clave] = valor;
        lock (Cerrojo)
            Mapa[pid] = new Entrada { Datos = datos };
    }

    // --- hacer el vídeo -----------------------------------------------------------

    /// <summary>
    /// Lanza el montaje. Antes deja en el proyecto lo que el motor necesita saber: si lleva
    /// voz, dónde van los vídeos y los ajustes de ese momento.
    /// </summary>
    public static void HacerVideo(ServerOptions o, string pid, bool voz)
    {
        var p = Proyectos.Leer(o, pid);
        p["voz"] = voz;
        p["mis_videos"] = o.MisVideos;
        p["ajustes"] = Proyectos.LeerAjustes(o);
        Proyectos.Guardar(o, pid, p);
        List<string> argv = ["video", Proyectos.Carpeta(o, pid)];
        if (voz) argv.Add("--voz");
        var proc = Lanzar(o, pid, argv);
        lock (Cerrojo)
            Mapa[pid] = new Entrada
            {
                Datos = new JsonObject { ["estado"] = "haciendo", ["msg"] = "Empezando…", ["p"] = 0.02 },
                Proc = proc,
            };

        JsonObject fin = new();
        List<string> cola = []; // las últimas líneas que no son JSON: el rastro del fallo
        Escuchar(proc, linea =>
        {
            linea = linea.Trim();
            if (Linea(linea) is not { } d)
            {
                cola.Add(linea);
                if (cola.Count > 20) cola.RemoveAt(0);
                return;
            }
            lock (Cerrojo)
            {
                if (!EsMio(pid, proc)) return;
                if (d.ContainsKey("msg"))
                {
                    var t = Mapa[pid].Datos;
                    t["msg"] = d["msg"]?.DeepClone();
                    t["p"] = d["p"]?.DeepClone() ?? 0;
                }
                if (d.ContainsKey("ok") || d.ContainsKey("error"))
                    fin = d;
            }
        }, () =>
        {
            lock (Cerrojo)
            {
                if (!EsMio(pid, proc)) return;
                if (Pasos.Bool(fin, "ok"))
                {
                    var q = Proyectos.Leer(o, pid);
                    q["video"] = fin["video"]?.DeepClone();
                    q["srt"] = fin["srt"]?.DeepClone();
                    q["fase"] = "video";
                    q["hecho"] = Proyectos.Hecho();
                    Proyectos.Guardar(o, pid, q);
                    Mapa[pid] = new Entrada { Datos = Estado("listo") };
                }
                else
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(Proyectos.Carpeta(o, pid), "ultimo-error.txt"),
                            string.Join("\n", cola), new UTF8Encoding(false));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // Sin rastro escrito, pero el error le llega igual a la persona.
                    }
                    var error = Pasos.Str(fin, "error");
                    Mapa[pid] = new Entrada
                    {
                        Datos = new JsonObject
                        {
                            ["estado"] = "error",
                            ["paso"] = fin["paso"]?.DeepClone(),
                            ["msg"] = error.Length != 0 ? error : "El vídeo no se ha podido hacer.",
                        },
                    };
                }
            }
        });
    }

    /// <summary>Olvida la tarea de un vídeo y mata su proceso si sigue vivo.</summary>
    public static void Cancelar(string pid)
    {
        Entrada? t;
        lock (Cerrojo)
        {
            if (!Mapa.Remove(pid, out t)) return;
        }
        Matar(t.Proc);
    }

    /// <summary>Al cerrar la ventana: ningún grabador ni montaje sobrevive a la app.</summary>
    public static void CancelarTodas()
    {
        List<string> ids;
        lock (Cerrojo) ids = Mapa.Keys.ToList();
        foreach (var pid in ids) Cancelar(pid);
        lock (Oyendo)
        {
            foreach (var proc in Oyendo) Matar(proc);
            Oyendo.Clear();
        }
    }

    // Se mata el árbol entero: el motor arrastra al Chromium y al node de Playwright, que si
    // quedaran huérfanos seguirían con la ventana de la web abierta.
    private static void Matar(Process? proc)
    {
        if (proc is null) return;
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            // Ya había terminado o se está yendo.
        }
    }

    /// <summary>Lee una frase con la voz y velocidad de los ajustes; corta la anterior si seguía sonando.</summary>
    public static void Oir(ServerOptions o, string texto)
    {
        var a = Proyectos.LeerAjustes(o);
        var peticion = new JsonObject
        {
            ["texto"] = texto,
            ["voz"] = a["voz"]?.DeepClone(),
            ["velocidad"] = a["velocidad"]?.DeepClone(),
        };
        lock (Oyendo)
        {
            foreach (var proc in Oyendo) Matar(proc);
            Oyendo.Clear();
            var psi = Preparar(o, ["oir", Json.Dump(peticion, indent: false)], null, tuberias: false);
            if (Process.Start(psi) is { } nuevo)
                Oyendo.Add(nuevo);
        }
    }

    /// <summary>
    /// Las voces instaladas, preguntadas una sola vez al motor (la última línea JSON de su
    /// salida). Si algo falla, una lista vacía: la app funciona sin voz.
    /// </summary>
    public static List<string> Voces(ServerOptions o)
    {
        lock (Oyendo)
        {
            if (Voces_ is not null) return Voces_;
        }
        List<string> voces = [];
        try
        {
            var psi = Preparar(o, ["voces"], null, tuberias: true);
            using var proc = Process.Start(psi)!;
            // Los errores se vacían aparte para que una traza larga no atasque la tubería.
            proc.BeginErrorReadLine();
            var salida = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            var ultima = salida.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length != 0);
            if (ultima is not null && Json.Parse(ultima) is JsonObject d && d["voces"] is JsonArray lista)
                voces = lista.Select(v => v?.ToString() ?? "").ToList();
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException
                                  or System.ComponentModel.Win32Exception)
        {
            voces = [];
        }
        lock (Oyendo)
            return Voces_ ??= voces;
    }

    /// <summary>Para los tests: olvida la caché de voces y cualquier tarea de otra prueba.</summary>
    internal static void Reiniciar()
    {
        CancelarTodas();
        lock (Oyendo) Voces_ = null;
    }
}
