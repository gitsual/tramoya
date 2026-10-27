using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tramoya.Core;
using Tramoya.Voice;

namespace Tramoya.Easy;

/// <summary>
/// Dónde vive todo lo de «tramoya easy» y cómo se lanza el motor. En el Python son globales
/// calculadas al importar (<c>KIT</c>, <c>CASA</c>, <c>DATOS</c>, <c>MIS_VIDEOS</c>, <c>MOTOR</c>,
/// <c>ENTORNO</c>); aquí las decide quien arranca el servidor (la ventana, el CLI o un test).
/// </summary>
public sealed class ServerOptions
{
    /// <summary>La carpeta de datos: un proyecto por subcarpeta, <c>ajustes.json</c> y el perfil <c>_navegador</c>.</summary>
    public required string Datos { get; init; }

    /// <summary>«Mis vídeos»: donde el motor deja los mp4 terminados.</summary>
    public required string MisVideos { get; init; }

    /// <summary>La carpeta de la app (el kit o <c>~/Tramoya</c>); es el directorio de trabajo del acceso directo.</summary>
    public required string Casa { get; init; }

    /// <summary>El prefijo del motor, p. ej. <c>["C:\…\Tramoya.exe", "easy-engine"]</c>; se le añaden los argumentos del Python.</summary>
    public required string[] Motor { get; init; }

    /// <summary>Variables que se añaden al entorno de los procesos hijos (<c>PLAYWRIGHT_BROWSERS_PATH</c>, <c>PATH</c>…).</summary>
    public IDictionary<string, string> Entorno { get; init; } = new Dictionary<string, string>();

    /// <summary>El ejecutable al que apunta el acceso directo «Tramoya»; sin él no se crean accesos.</summary>
    public string? ExePath { get; init; }

    /// <summary>Si la interfaz debe enseñar lo que solo tiene sentido en Windows (acceso directo, explorador).</summary>
    public bool Windows { get; init; } = true;

    /// <summary>Kit portátil: al arrancar se deja un acceso directo con icono junto al exe, en <see cref="Casa"/>.</summary>
    public bool Kit { get; init; }
}

/// <summary>
/// El servidor HTTP local de «tramoya easy», port de la clase <c>H</c> y de <c>main</c> de
/// <c>tramoya/easy/app.py</c>: sirve <c>ui/index.html</c> (incrustado) y la misma API
/// <c>/api/…</c> con los mismos JSON y códigos, solo en 127.0.0.1 y en un puerto libre. La
/// ventana (WebView2) o el modo <c>--no-window</c> lo arrancan con <see cref="Start"/>.
/// </summary>
public sealed class Server : IDisposable
{
    private static readonly Regex RutaProyecto = new("^/api/p/([a-z0-9-]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex RutaVideo = new(@"^/video/([a-z0-9-]+)\.mp4$", RegexOptions.CultureInvariant);
    private static readonly Regex RutaAccion = new("^/api/p/([a-z0-9-]+)/([a-z-]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Rango = new(@"^bytes=(\d*)-(\d*)", RegexOptions.CultureInvariant);

    private readonly ServerOptions _o;
    private readonly HttpListener _listener;
    private readonly Thread _bucle;
    private int _parado;

    public string Url { get; }

    private Server(ServerOptions o, HttpListener listener, string url)
    {
        _o = o;
        _listener = listener;
        Url = url;
        _bucle = new Thread(Aceptar) { IsBackground = true, Name = "tramoya-easy-http" };
    }

    /// <summary>
    /// Arranca el servidor en un puerto libre de 127.0.0.1 y vuelve enseguida: las peticiones
    /// se atienden en el pool de hilos. Crea las carpetas, calienta la lista de voces en un
    /// hilo aparte (tarda) y, en el kit, deja el acceso directo con icono.
    /// </summary>
    public static Server Start(ServerOptions options)
    {
        Directory.CreateDirectory(options.Datos);
        Directory.CreateDirectory(options.MisVideos);
        var (listener, url) = Escuchar();
        var servidor = new Server(options, listener, url);
        servidor._bucle.Start();
        // Una excepción en un hilo suelto tiraría la app entera; sin voces se sigue adelante.
        new Thread(() => { try { Tareas.Voces(options); } catch (Exception) { } }) { IsBackground = true }.Start();
        if (options.Kit && options.Windows && options.ExePath is not null
            && !File.Exists(Path.Combine(options.Casa, "Tramoya.lnk")))
        {
            try { servidor.CrearAcceso(options.Casa); }
            catch (Exception) { /* en el kit, un acceso directo con icono; si no se puede, nada */ }
        }
        return servidor;
    }

    /// <summary>
    /// El modo sin ventana (<c>--no-window</c> / <c>--sin-ventana</c>): imprime la dirección y
    /// se queda escuchando hasta que maten el proceso. La ventana de WebView2 no pasa por
    /// aquí: usa <see cref="Start"/> y <see cref="Stop"/> al cerrarse.
    /// </summary>
    public static int Run(ServerOptions options, bool noWindow)
    {
        using var servidor = Start(options);
        if (noWindow)
        {
            Console.Out.WriteLine(servidor.Url);
            Console.Out.Flush();
        }
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    /// <summary>Para el servidor y mata los grabadores y montajes que sigan en marcha.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _parado, 1) != 0) return;
        Tareas.CancelarTodas();
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        try { _listener.Close(); } catch (ObjectDisposedException) { }
    }

    public void Dispose() => Stop();

    // Un puerto libre, como `puerto_libre()`: se pide el 0 y se mira cuál ha tocado. Entre
    // soltarlo y que HttpListener lo coja podría colárselo otro proceso; se reintenta.
    private static (HttpListener, string) Escuchar()
    {
        HttpListenerException? ultimo = null;
        for (var intento = 0; intento < 5; intento++)
        {
            var puerto = PuertoLibre();
            var url = $"http://127.0.0.1:{puerto}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            try
            {
                listener.Start();
                return (listener, url);
            }
            catch (HttpListenerException e)
            {
                ultimo = e;
                listener.Close();
            }
        }
        throw ultimo!;
    }

    private static int PuertoLibre()
    {
        using var s = new TcpListener(IPAddress.Loopback, 0);
        s.Start();
        return ((IPEndPoint)s.LocalEndpoint).Port;
    }

    private void Aceptar()
    {
        while (_parado == 0)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = _listener.GetContext();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                break; // Stop()
            }
            ThreadPool.QueueUserWorkItem(_ => Atender(ctx));
        }
    }

    private void Atender(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod == "GET") Get(ctx);
            else if (ctx.Request.HttpMethod == "POST") Post(ctx);
            else Error(ctx, 501);
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
        {
            // El cliente cerró antes de tiempo (la ventana se fue); no hay a quién contestar.
        }
        catch (Exception e)
        {
            // En el Python una excepción aquí corta la conexión sin respuesta; mejor un 500
            // con el motivo, que la interfaz enseña como error.
            try { Responder(ctx, new JsonObject { ["error"] = e.Message }, 500); }
            catch (Exception) { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch (Exception) { }
        }
    }

    // --- respuestas --------------------------------------------------------------------

    private static void Responder(HttpListenerContext ctx, JsonNode? datos, int code = 200)
    {
        var cuerpo = Encoding.UTF8.GetBytes(Json.Dump(datos, indent: false));
        var r = ctx.Response;
        r.StatusCode = code;
        r.ContentType = "application/json; charset=utf-8";
        r.Headers["Cache-Control"] = "no-store";
        r.ContentLength64 = cuerpo.Length;
        r.OutputStream.Write(cuerpo);
    }

    /// <summary>Lo que hace <c>send_error</c>: el código con una página mínima.</summary>
    private static void Error(HttpListenerContext ctx, int code)
    {
        var r = ctx.Response;
        r.StatusCode = code;
        r.ContentType = "text/html; charset=utf-8";
        var cuerpo = Encoding.UTF8.GetBytes($"<!doctype html><title>{code}</title><h1>Error {code}</h1>");
        r.ContentLength64 = cuerpo.Length;
        r.OutputStream.Write(cuerpo);
    }

    private static byte[] Recurso(string nombre)
    {
        using var s = typeof(Server).Assembly.GetManifestResourceStream(nombre)
            ?? throw new FileNotFoundException($"Falta el recurso incrustado {nombre}");
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    private static readonly Lazy<byte[]> Ui = new(() => Recurso("ui/index.html"));

    /// <summary>El icono, sacado del exe a la carpeta de datos una sola vez: un .lnk necesita un fichero.</summary>
    private string Icono()
    {
        var ruta = Path.Combine(_o.Datos, "tramoya.ico");
        if (!File.Exists(ruta))
        {
            try { File.WriteAllBytes(ruta, Recurso("ui/tramoya.ico")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FileNotFoundException) { }
        }
        return ruta;
    }

    private string? Escritorio() => _o.Windows ? Sapi.Escritorio() : null;

    /// <summary>Un acceso directo «Tramoya» con su icono, que abre la app sin consola.</summary>
    private string CrearAcceso(string? carpeta)
    {
        if (carpeta is null) throw new InvalidOperationException("no hay escritorio");
        if (_o.ExePath is null) throw new InvalidOperationException("no sé qué ejecutable abrir");
        return Sapi.CrearAcceso(carpeta, _o.ExePath, _o.Casa, Icono());
    }

    private static void AbrirArchivo(string ruta) =>
        Process.Start(new ProcessStartInfo(ruta) { UseShellExecute = true });

    // --- GET ---------------------------------------------------------------------------

    private void Get(HttpListenerContext ctx)
    {
        var ruta = ctx.Request.Url?.AbsolutePath ?? "/";
        if (ruta is "/" or "/index.html")
        {
            var r = ctx.Response;
            r.StatusCode = 200;
            r.ContentType = "text/html; charset=utf-8";
            r.Headers["Cache-Control"] = "no-store";
            r.ContentLength64 = Ui.Value.Length;
            r.OutputStream.Write(Ui.Value);
            return;
        }
        if (ruta == "/api/inicio")
        {
            var esc = Escritorio();
            var voces = new JsonArray();
            foreach (var nombre in Tareas.Voces(_o)) voces.Add(nombre);
            Responder(ctx, new JsonObject
            {
                ["proyectos"] = Proyectos.Lista(_o),
                ["voces"] = voces,
                ["webs"] = Proyectos.Webs(_o),
                ["ajustes"] = Proyectos.LeerAjustes(_o),
                ["windows"] = _o.Windows,
                ["carpeta"] = _o.MisVideos,
                ["acceso"] = esc is not null && File.Exists(Path.Combine(esc, "Tramoya.lnk")),
                // Solo con un modelo a mano se ofrece «pídeselo a la IA»; el Python manda false.
                ["agente"] = Tareas.HayModelo(_o),
            });
            return;
        }
        if (RutaProyecto.Match(ruta) is { Success: true } m)
        {
            if (!Proyectos.Existe(_o, m.Groups[1].Value))
            {
                Responder(ctx, new JsonObject { ["error"] = "no existe" }, 404);
                return;
            }
            var p = Proyectos.Leer(_o, m.Groups[1].Value);
            p["tarea"] = Tareas.Tarea(m.Groups[1].Value);
            Responder(ctx, p);
            return;
        }
        if (RutaVideo.Match(ruta) is { Success: true } v)
        {
            Video(ctx, v.Groups[1].Value);
            return;
        }
        Error(ctx, 404);
    }

    /// <summary>El mp4 del proyecto para el <c>&lt;video&gt;</c> de la interfaz, con rangos para poder saltar.</summary>
    private void Video(HttpListenerContext ctx, string pid)
    {
        if (!Proyectos.Existe(_o, pid))
        {
            Error(ctx, 404);
            return;
        }
        var p = Proyectos.Leer(_o, pid);
        var ruta = Pasos.Str(p, "video");
        if (ruta.Length == 0 || !File.Exists(ruta))
        {
            Error(ctx, 404);
            return;
        }
        var total = new FileInfo(ruta).Length;
        long ini = 0, fin = total - 1;
        var r = ctx.Response;
        var rango = ctx.Request.Headers["Range"];
        if (rango is not null && Rango.Match(rango) is { Success: true } m)
        {
            ini = m.Groups[1].Value.Length != 0 ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            fin = m.Groups[2].Value.Length != 0 ? long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : total - 1;
            if (fin > total - 1) fin = total - 1; // un final más allá del fichero sería un Content-Length falso
            r.StatusCode = 206;
            r.Headers["Content-Range"] = $"bytes {ini}-{fin}/{total}";
        }
        else
        {
            r.StatusCode = 200;
        }
        r.ContentType = "video/mp4";
        r.Headers["Accept-Ranges"] = "bytes";
        r.ContentLength64 = fin - ini + 1;
        using var f = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        f.Seek(ini, SeekOrigin.Begin);
        var quedan = fin - ini + 1;
        var trozo = new byte[1 << 16];
        try
        {
            while (quedan > 0)
            {
                var n = f.Read(trozo, 0, (int)Math.Min(trozo.Length, quedan));
                if (n == 0) break;
                r.OutputStream.Write(trozo, 0, n);
                quedan -= n;
            }
        }
        catch (Exception e) when (e is HttpListenerException or IOException)
        {
            // El reproductor cortó la descarga (saltó a otro punto): normal.
        }
    }

    // --- POST --------------------------------------------------------------------------

    private static JsonObject Cuerpo(HttpListenerContext ctx)
    {
        using var lector = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var texto = lector.ReadToEnd();
        if (texto.Trim().Length == 0) return new JsonObject();
        return Json.Parse(texto) as JsonObject ?? new JsonObject();
    }

    private void Post(HttpListenerContext ctx)
    {
        var cuerpo = Cuerpo(ctx);
        var ruta = ctx.Request.Url?.AbsolutePath ?? "/";
        if (ruta == "/api/ajustes")
        {
            Responder(ctx, Proyectos.GuardarAjustes(_o, cuerpo));
            return;
        }
        if (ruta == "/api/oir")
        {
            var texto = Pasos.Str(cuerpo, "texto").Trim();
            if (texto.Length != 0)
                Tareas.Oir(_o, texto.Length > 500 ? texto[..500] : texto);
            Responder(ctx, new JsonObject { ["ok"] = true });
            return;
        }
        if (ruta == "/api/acceso")
        {
            try
            {
                Responder(ctx, new JsonObject { ["ok"] = true, ["ruta"] = CrearAcceso(Escritorio()) });
            }
            catch (Exception exc)
            {
                Responder(ctx, new JsonObject { ["error"] = $"No he podido crear el acceso directo ({exc.Message})" });
            }
            return;
        }
        if (ruta == "/api/crear")
        {
            try
            {
                var nuevo = Proyectos.Crear(_o, Pasos.Str(cuerpo, "nombre"), Pasos.Str(cuerpo, "url"));
                Responder(ctx, new JsonObject { ["id"] = nuevo });
            }
            catch (ArgumentException e)
            {
                Responder(ctx, new JsonObject { ["error"] = e.Message }, 400);
            }
            return;
        }
        if (ruta == "/api/carpeta")
        {
            Directory.CreateDirectory(_o.MisVideos);
            AbrirArchivo(_o.MisVideos);
            Responder(ctx, new JsonObject { ["ok"] = true });
            return;
        }
        var m = RutaAccion.Match(ruta);
        if (!m.Success || !Proyectos.Existe(_o, m.Groups[1].Value))
        {
            Responder(ctx, new JsonObject { ["error"] = "no existe" }, 404);
            return;
        }
        var (pid, accion) = (m.Groups[1].Value, m.Groups[2].Value);
        lock (Tareas.Cerrojo)
        {
            switch (accion)
            {
                case "abrir-web":
                    Tareas.Cancelar(pid);
                    Tareas.AbrirWeb(_o, pid);
                    break;
                case "agente":
                {
                    var objetivo = Pasos.Str(cuerpo, "objetivo").Trim();
                    if (objetivo.Length == 0)
                    {
                        Responder(ctx, new JsonObject { ["error"] = "Escribe qué quieres que haga la IA." }, 400);
                        return;
                    }
                    Tareas.Cancelar(pid);
                    Tareas.Agente(_o, pid, objetivo);
                    break;
                }
                case "empezar" or "terminar":
                    Tareas.Orden(pid, accion);
                    break;
                case "cancelar":
                    Tareas.Cancelar(pid);
                    break;
                case "guardar":
                {
                    var p = Proyectos.Leer(_o, pid);
                    foreach (var clave in new[] { "nombre", "url", "pasos", "fase" })
                        if (cuerpo.ContainsKey(clave))
                            p[clave] = cuerpo[clave]?.DeepClone();
                    Proyectos.Guardar(_o, pid, p);
                    break;
                }
                case "video":
                    Tareas.Cancelar(pid);
                    Tareas.HacerVideo(_o, pid, Pasos.Bool(cuerpo, "voz"));
                    break;
                case "abrir":
                {
                    var p = Proyectos.Leer(_o, pid);
                    var video = Pasos.Str(p, "video");
                    if (Pasos.Str(cuerpo, "que") == "carpeta" || video.Length == 0)
                    {
                        Directory.CreateDirectory(_o.MisVideos);
                        if (video.Length != 0 && File.Exists(video) && _o.Windows)
                            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{video}\"", UseShellExecute = false });
                        else
                            AbrirArchivo(_o.MisVideos);
                    }
                    else
                    {
                        AbrirArchivo(video);
                    }
                    break;
                }
                case "duplicar":
                    Responder(ctx, new JsonObject { ["ok"] = true, ["id"] = Proyectos.Duplicar(_o, pid) });
                    return;
                case "borrar":
                    Tareas.Cancelar(pid);
                    try { Directory.Delete(Proyectos.Carpeta(_o, pid), recursive: true); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    break;
                default:
                    Responder(ctx, new JsonObject { ["error"] = "acción desconocida" }, 400);
                    return;
            }
        }
        Responder(ctx, new JsonObject { ["ok"] = true });
    }
}
